using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using XilousProbe.Settings;

namespace XilousProbe.Cdp;

/// <summary>
/// Starts the probe Chrome: the user's own Chrome install, on a dedicated profile
/// folder with DevTools enabled. Chrome 136+ refuses remote debugging on the
/// default profile, so the probe profile is permanent instead: sign in once and
/// Chrome sync brings bookmarks, extensions and passwords, and logins persist.
/// It runs alongside the user's everyday Chrome.
/// </summary>
public static class ChromeLauncher
{
    public static string ProfileDir { get; } = Path.Combine(SettingsStore.LocalDataDir, "ChromeProfile");

    public static string? FindChrome()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> BuildArguments(string profileDir, string? url) =>
    [
        $"--user-data-dir={profileDir}",
        // 0 lets Chrome pick a free port; it writes the choice to DevToolsActivePort.
        "--remote-debugging-port=0",
        "--no-first-run",
        "--no-default-browser-check",
        .. string.IsNullOrEmpty(url) ? Array.Empty<string>() : [url],
    ];

    /// <summary>The port in a DevToolsActivePort file: the first line, then the browser's WebSocket path.</summary>
    public static int? ParseActivePort(string? fileText)
    {
        var first = fileText?.Split('\n', 2)[0].Trim();
        return int.TryParse(first, out int port) && port is > 0 and < 65536 ? port : null;
    }

    public static string ActivePortFile => Path.Combine(ProfileDir, "DevToolsActivePort");

    /// <summary>The port of a probe Chrome that is already running, if one is.</summary>
    public static async Task<int?> FindRunningPortAsync()
    {
        try
        {
            if (!File.Exists(ActivePortFile)) return null;
            var port = ParseActivePort(await File.ReadAllTextAsync(ActivePortFile));
            if (port is null) return null;
            return await CdpBrowser.ProbeAsync(port.Value) is not null ? port : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Starts the probe Chrome and returns its DevTools port once Chrome reports it.</summary>
    public static async Task<int> LaunchAsync(string? url, CancellationToken ct = default)
    {
        var chrome = FindChrome() ?? throw new FileNotFoundException("Google Chrome is not installed, or not where Windows registers it.");
        Directory.CreateDirectory(ProfileDir);
        // A stale file from the last session would hand back a dead port.
        try { File.Delete(ActivePortFile); } catch (IOException) { }

        var psi = new ProcessStartInfo(chrome) { UseShellExecute = false };
        foreach (var arg in BuildArguments(ProfileDir, url)) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Chrome did not start.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(ActivePortFile) && ParseActivePort(await File.ReadAllTextAsync(ActivePortFile, ct)) is { } port)
                    return port;
            }
            catch (IOException) { /* Chrome is still writing it */ }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Chrome started but never opened its DevTools port.");
    }
}
