using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace XilousProbe.Update;

/// <summary>A release on GitHub that is newer than the running app.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Keeps the published XilousProbe.exe current with the repo's GitHub releases.
/// The release workflow builds with -p:ReleaseBuild=true; only those builds update
/// themselves, so a dev build never replaces itself with a release.
///
/// The swap relies on Windows letting a running .exe be renamed: the running file
/// becomes XilousProbe.exe.old, the download takes its name, the app restarts into
/// it, and the next start deletes the .old file.
/// </summary>
public sealed class Updater
{
    public const string Repo = "Xilous/xilous-ui-probe";
    public const string AssetName = "XilousProbe.exe";

    private static readonly HttpClient Http = CreateClient();

    public static bool IsReleaseBuild =>
#if RELEASE_BUILD
        true;
#else
        false;
#endif

    public static Version CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XilousProbe-Updater", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>"v0.1.12" or "0.1.12" to a version; null for anything else.</summary>
    public static Version? ParseTag(string? tag)
    {
        var s = tag?.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    /// <summary>Reads a GitHub "latest release" response. Null when it is not newer or has no usable asset.</summary>
    public static AvailableUpdate? FromRelease(JsonObject release, Version current)
    {
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = release["tag_name"]?.GetValue<string>();
        var version = ParseTag(tag);
        if (version is null || version <= current) return null;
        foreach (var asset in release["assets"] as JsonArray ?? [])
        {
            if (asset?["name"]?.GetValue<string>() != AssetName) continue;
            var url = asset["browser_download_url"]?.GetValue<string>();
            if (url is null || !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal)) return null;
            var digest = asset["digest"]?.GetValue<string>();
            var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..].ToLowerInvariant() : null;
            long.TryParse(asset["size"]?.ToJsonString(), out long size);
            return new AvailableUpdate(version, tag!, new Uri(url), size, sha);
        }
        return null;
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!res.IsSuccessStatusCode) return null; // 404 before the first release; 403 when rate-limited
        var release = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject;
        return release is null ? null : FromRelease(release, CurrentVersion);
    }

    /// <summary>
    /// Downloads the update next to the running exe, verifies it, and swaps it in.
    /// Returns the path to start. The running app keeps working until it restarts.
    /// </summary>
    /// <param name="progress">The download's progress, 0 to 1, then 1 once it is verified.</param>
    /// <param name="verifying">Called when the download is complete and its digest is being checked.</param>
    public async Task<string> DownloadAndStageAsync(AvailableUpdate update, IProgress<double>? progress = null,
        Action? verifying = null, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");
        var dir = Path.GetDirectoryName(exe)!;
        var incoming = Path.Combine(dir, AssetName + ".new");
        var old = exe + ".old";

        await using (var file = File.Create(incoming))
        {
            using var res = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            long total = update.Size > 0 ? update.Size : res.Content.Headers.ContentLength ?? 0;
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        verifying?.Invoke();

        var info = new FileInfo(incoming);
        if (update.Size > 0 && info.Length != update.Size)
        {
            File.Delete(incoming);
            throw new InvalidDataException($"The download is {info.Length} bytes, the release says {update.Size}.");
        }
        if (update.Sha256 is not null)
        {
            await using var check = File.OpenRead(incoming);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            if (actual != update.Sha256)
            {
                File.Delete(incoming);
                throw new InvalidDataException("The download does not match the release's SHA-256 digest.");
            }
        }

        // Rename the running exe out of the way, then move the new one into its place.
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(incoming, exe);
        }
        catch
        {
            File.Move(old, exe); // put the working copy back
            throw;
        }
        return exe;
    }

    /// <summary>Starts the updated exe, which waits for this process to exit before it takes over.</summary>
    public static void Relaunch(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>On start: wait out the previous version if it is still closing, and delete its file.</summary>
    public static void FinishPreviousUpdate(string[] args)
    {
        int i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); }
            catch (ArgumentException) { /* already gone */ }
        }
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, AssetName + ".new"));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    public static string InformationalVersion =>
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? CurrentVersion.ToString();
}
