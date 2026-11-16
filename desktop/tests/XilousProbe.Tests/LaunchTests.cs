using System.IO;
using System.Text.Json.Nodes;
using XilousProbe.Cdp;
using XilousProbe.Launcher;
using XilousProbe.Settings;

namespace XilousProbe.Tests;

public class ChromeLauncherTests
{
    [Theory]
    [InlineData("53811\n/devtools/browser/abc", 53811)]
    [InlineData("9222\r\n/devtools/browser/x", 9222)]
    [InlineData("", null)]
    [InlineData("not a port", null)]
    [InlineData("70000\n/x", null)]
    [InlineData(null, null)]
    public void Reads_the_active_port_file(string? text, int? expected) =>
        Assert.Equal(expected, ChromeLauncher.ParseActivePort(text));

    [Fact]
    public void Launches_on_the_probe_profile_with_a_chosen_port()
    {
        var args = ChromeLauncher.BuildArguments(@"C:\p", "https://example.com");
        Assert.Equal(@"--user-data-dir=C:\p", args[0]);
        Assert.Contains("--remote-debugging-port=0", args);
        Assert.Equal("https://example.com", args[^1]);
        Assert.DoesNotContain(args, a => a.StartsWith("--enable-automation"));
    }

    [Fact]
    public void Leaves_the_url_off_when_there_is_none() =>
        Assert.DoesNotContain(ChromeLauncher.BuildArguments(@"C:\p", null), a => a.StartsWith("http"));
}

public class AppLauncherTests
{
    [Fact]
    public void Runs_a_dev_command_through_cmd_with_the_port_filled_in()
    {
        var entry = new AppEntry { Kind = AppKind.DevCommand, Command = "npm", Arguments = "run dev -- --remoteDebuggingPort {port}", Port = 9333 };
        var (file, args) = AppLauncher.BuildCommand(entry)!.Value;
        Assert.Equal("cmd.exe", file);
        Assert.Equal("/d /s /c \"npm run dev -- --remoteDebuggingPort 9333\"", args);
    }

    [Fact]
    public void Adds_the_debugging_switch_to_an_executable()
    {
        var entry = new AppEntry { Kind = AppKind.Executable, Command = @"C:\Apps\Thing.exe", Arguments = "--flag", Port = 9400 };
        Assert.Equal((@"C:\Apps\Thing.exe", "--flag --remote-debugging-port=9400"), AppLauncher.BuildCommand(entry));
    }

    [Fact]
    public void Does_not_add_the_switch_twice()
    {
        var entry = new AppEntry { Kind = AppKind.Executable, Command = "a.exe", Arguments = "--remote-debugging-port={port}", Port = 9401 };
        Assert.Equal(("a.exe", "--remote-debugging-port=9401"), AppLauncher.BuildCommand(entry));
    }

    [Fact]
    public void Attach_starts_nothing() =>
        Assert.Null(AppLauncher.BuildCommand(new AppEntry { Kind = AppKind.Attach, Port = 9222 }));
}

public class UrlTests
{
    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  localhost:5173/pricing ", "http://localhost:5173/pricing")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://intranet/app", "http://intranet/app")]
    [InlineData("file:///C:/x.html", "file:///C:/x.html")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Normalizes_what_people_type(string raw, string? expected) =>
        Assert.Equal(expected, LauncherWindow.NormalizeUrl(raw));
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xp-tests-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Web_prefs_round_trip_and_report_only_real_changes()
    {
        var store = new SettingsStore(PathIn);
        var events = new List<IReadOnlyDictionary<string, JsonNode?>>();
        store.WebPrefsChanged += events.Add;

        store.SetWebPrefs(new JsonObject { ["theme"] = "nord", ["copyDepth"] = 2 });
        store.SetWebPrefs(new JsonObject { ["theme"] = "nord" }); // unchanged: no event
        Assert.Single(events);

        var reloaded = new SettingsStore(PathIn);
        var all = reloaded.GetWebPrefs(null);
        Assert.Equal("nord", all["theme"]!.GetValue<string>());
        Assert.Equal(2, all["copyDepth"]!.GetValue<int>());
        Assert.Single(reloaded.GetWebPrefs(["theme", "missing"]));

        store.RemoveWebPrefs(["theme"]);
        Assert.Null(events[^1]["theme"]);
        Assert.False(store.GetWebPrefs(null).ContainsKey("theme"));
    }

    [Fact]
    public void A_damaged_file_does_not_stop_the_app()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ not json");
        var store = new SettingsStore(PathIn);
        Assert.Empty(store.GetWebPrefs(null));
        Assert.True(File.Exists(PathIn + ".broken"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
