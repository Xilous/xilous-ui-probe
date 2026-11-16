using XilousProbe.Theme;

namespace XilousProbe.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Without_the_usage_monitor_it_follows_windows()
    {
        Assert.Equal(XilousTheme.Presets["graphite"], XilousTheme.ResolveRoles(null, systemDark: true));
        Assert.Equal(XilousTheme.Presets["paper"], XilousTheme.ResolveRoles(null, systemDark: false));
        Assert.Equal(XilousTheme.Presets["paper"], XilousTheme.ResolveRoles("not json", systemDark: false));
    }

    [Fact]
    public void It_wears_the_monitors_preset()
    {
        var json = """{ "appearance": { "theme": "black_and_gold", "widget_size": 200 } }""";
        Assert.Equal(XilousTheme.Presets["black_and_gold"], XilousTheme.ResolveRoles(json, systemDark: false));
        var system = """{ "appearance": { "theme": "system" } }""";
        Assert.Equal(XilousTheme.Presets["paper"], XilousTheme.ResolveRoles(system, systemDark: false));
    }

    [Fact]
    public void It_wears_the_monitors_custom_colours()
    {
        var json = """
        { "appearance": { "theme": "custom", "roles": {
            "background": "#101010", "text": "#FAFAFA", "session": "#00FF00", "weekly": "#FF0000",
            "fable": "#0000FF", "cpu": "#FFFFFF", "over_pace": "#FFAA00", "far_over": "#FF00FF" } } }
        """;
        var r = XilousTheme.ResolveRoles(json, systemDark: true);
        Assert.Equal(0x101010u, r.Background);
        Assert.Equal(0xFF0000u, r.Weekly);
        Assert.NotEqual(r.Cpu, r.Ram);
        Assert.Equal(XilousTheme.ToColor(0xFF0000), XilousTheme.Build(r).Accent);
    }

    [Fact]
    public void Settings_from_before_themes_read_as_the_monitor_migrates_them()
    {
        var midnight = """{ "appearance": { "mode": "custom", "colors": [1382691, 15199735, 15772295, 10926591, 7657683] } }""";
        Assert.Equal(XilousTheme.Presets["midnight"], XilousTheme.ResolveRoles(midnight, systemDark: false));
        Assert.Equal(XilousTheme.Presets["paper"], XilousTheme.ResolveRoles("""{ "appearance": { "mode": "light" } }""", true));
    }

    [Fact]
    public void Every_preset_keeps_text_legible_and_measuring_apart_from_selecting()
    {
        foreach (var (name, r) in XilousTheme.Presets)
        {
            Assert.True(XilousTheme.Contrast(r.Text, r.Background) >= 4.5, name);
            var p = XilousTheme.Build(r);
            Assert.NotEqual(p.Accent, p.Measure);
            uint Rgb(System.Windows.Media.Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);
            Assert.True(XilousTheme.Contrast(Rgb(p.OnAccent), Rgb(p.Accent)) >= 3, name);
        }
    }
}
