using System.IO;
using XilousProbe.Settings;

namespace XilousProbe.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xp-tests-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, "settings.json");

    [Fact]
    public void The_widget_position_round_trips()
    {
        var store = new SettingsStore(PathIn);
        store.Data.WidgetX = 2560;
        store.Data.WidgetY = 533.5;
        store.Data.WidgetHidden = true;
        store.Save();

        var reloaded = new SettingsStore(PathIn).Data;
        Assert.Equal(2560, reloaded.WidgetX);
        Assert.Equal(533.5, reloaded.WidgetY);
        Assert.True(reloaded.WidgetHidden);
    }

    [Fact]
    public void Settings_from_earlier_versions_load_without_their_old_keys()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, """{ "LastUrl": "https://example.com", "Apps": [], "WebPrefs": { "theme": "nord" }, "WidgetX": 10, "WidgetY": 20 }""");
        var data = new SettingsStore(PathIn).Data;
        Assert.Equal(10, data.WidgetX);
        Assert.Equal(20, data.WidgetY);
        Assert.False(data.WidgetHidden);
    }

    [Fact]
    public void A_damaged_file_does_not_stop_the_app()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ not json");
        var data = new SettingsStore(PathIn).Data;
        Assert.Null(data.WidgetX);
        Assert.True(File.Exists(PathIn + ".broken"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
