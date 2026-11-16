using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XilousProbe.Theme;

/// <summary>
/// A theme's nine colour roles, as the Xilous Usage Monitor stores them:
/// background, text, the three usage windows (5h, 7d, Fable), CPU, RAM and the
/// two warning colours. Xilous Probe paints itself from the same roles.
/// </summary>
public sealed record Roles(
    uint Background, uint Text, uint Session, uint Weekly, uint Fable,
    uint Cpu, uint Ram, uint OverPace, uint FarOver);

/// <summary>Everything Xilous Probe is drawn with, worked out from a theme's roles.</summary>
public sealed record Palette(
    bool Dark, Color Bg, Color Ink, Color Dim, Color Muted, Color Faint, Color Line,
    Color Field, Color Track, Color Accent, Color OnAccent, Color Measure, Color OnMeasure,
    Color Error)
{
    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>
/// Xilous Probe wears the Xilous Usage Monitor's theme: whichever theme is chosen
/// in the monitor's appearance studio, or, without the monitor, the System theme,
/// which follows Windows between Graphite (dark) and Paper (light). It changes
/// live when the monitor's settings or the Windows theme change.
/// </summary>
public static class XilousTheme
{
    public const string SystemTheme = "system";
    public const string CustomTheme = "custom";

    /// <summary>The usage monitor's presets, identical to its appearance.rs.</summary>
    public static readonly IReadOnlyDictionary<string, Roles> Presets = new Dictionary<string, Roles>
    {
        ["graphite"] = new(0x1c1c1e, 0xeceff2, 0x22d3ee, 0x38a3f5, 0xa78bfa, 0x34d399, 0x2c936e, 0xfbbf24, 0xf87171),
        ["paper"] = new(0xf7f7f8, 0x1d2125, 0x0891b2, 0x1d6fd8, 0x7c3aed, 0x059669, 0x04694a, 0xb45309, 0xdc2626),
        ["midnight"] = new(0x151923, 0xe7edf7, 0xf6c9a8, 0xf0aa87, 0xa6b9ff, 0x74d8d3, 0x4f8f97, 0xffd27a, 0xff8a8a),
        ["porcelain"] = new(0xf6f3ed, 0x292d36, 0xb8683f, 0x9e4d32, 0x3859a8, 0x226c65, 0x174c47, 0xa16207, 0xb91c1c),
        ["evergreen"] = new(0x122522, 0xe0eee6, 0xf2d2ae, 0xeac098, 0x8fcada, 0x88d9b4, 0x5f9e84, 0xf5c451, 0xf59090),
        ["afterglow"] = new(0x281d2c, 0xf8e5ee, 0xffd0b0, 0xffb98f, 0xd3afff, 0xf19abf, 0xa76c8a, 0xffd166, 0xff7a90),
        ["black_and_gold"] = new(0x0b0b0c, 0xf3ead2, 0xf7d774, 0xd4af37, 0xb07a3b, 0xe5e4e2, 0x7d8c99, 0xff5a4e, 0xe0245e),
        ["red_and_white"] = new(0xffffff, 0x1a1a1a, 0xf04a55, 0xd7192a, 0x8e0e1b, 0x6b6b6b, 0x3d3d3d, 0xa86f00, 0x111827),
        ["mono"] = new(0x18181a, 0xf1f1f1, 0xd0d0d0, 0xd0d0d0, 0xd0d0d0, 0xd0d0d0, 0x8a8a8a, 0xfbbf24, 0xf87171),
        ["high_contrast"] = new(0x000000, 0xffffff, 0x00e5ff, 0x4da3ff, 0xc79bff, 0x3dff9e, 0x1fa866, 0xffd400, 0xff5c5c),
    };

    /// <summary>The four palettes the monitor offered before themes, by their colours.</summary>
    private static readonly (string Id, uint[] Colors)[] LegacyPresets =
    {
        ("midnight", new uint[] { 0x151923, 0xe7edf7, 0xf0aa87, 0xa6b9ff, 0x74d8d3 }),
        ("porcelain", new uint[] { 0xf6f3ed, 0x292d36, 0x9e4d32, 0x3859a8, 0x226c65 }),
        ("evergreen", new uint[] { 0x122522, 0xe0eee6, 0xeac098, 0x88d9b4, 0x8fcada }),
        ("afterglow", new uint[] { 0x281d2c, 0xf8e5ee, 0xffb98f, 0xd3afff, 0xf19abf }),
    };

    public static string UsageMonitorSettings { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XilousUsageMonitor", "settings.json");

    public static Palette Current { get; private set; } = Build(Presets["graphite"]);

    /// <summary>Raised on the UI thread when the theme changes.</summary>
    public static event Action? Changed;

    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _debounce;

    /// <summary>Loads the theme and starts following the monitor's settings and Windows.</summary>
    public static void Start(Dispatcher dispatcher)
    {
        Current = Load();
        var debounce = _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            var next = Load();
            if (next == Current) return;
            Current = next;
            Changed?.Invoke();
        };
        void Poke() => dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });

        var dir = Path.GetDirectoryName(UsageMonitorSettings)!;
        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Poke();
            _watcher.Created += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.Deleted += (_, _) => Poke();
        }
        catch (Exception) { /* No watching; the theme still loads at start. */ }
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) Poke();
        };
    }

    private static Palette Load()
    {
        string? json = null;
        try { if (File.Exists(UsageMonitorSettings)) json = File.ReadAllText(UsageMonitorSettings); }
        catch (IOException) { return Current; }
        catch (UnauthorizedAccessException) { return Current; }
        return Build(ResolveRoles(json, WindowsIsDark()));
    }

    public static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : true;
        }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// The roles in use from the usage monitor's settings file (its whole text, or null
    /// without one): its chosen preset, its custom colours, or System, which takes
    /// Graphite or Paper by `systemDark`. Settings from before themes are read the way
    /// the monitor migrates them.
    /// </summary>
    public static Roles ResolveRoles(string? settingsJson, bool systemDark)
    {
        var system = Presets[systemDark ? "graphite" : "paper"];
        if (string.IsNullOrWhiteSpace(settingsJson)) return system;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (!doc.RootElement.TryGetProperty("appearance", out var a) || a.ValueKind != JsonValueKind.Object) return system;

            if (a.TryGetProperty("theme", out var themeEl) && themeEl.ValueKind == JsonValueKind.String)
            {
                var theme = themeEl.GetString() ?? SystemTheme;
                if (theme == CustomTheme && a.TryGetProperty("roles", out var roles) && ReadRoles(roles) is { } custom) return custom;
                return Presets.TryGetValue(theme, out var preset) ? preset : system;
            }

            // Before themes: a mode and five colours.
            var mode = a.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            switch (mode)
            {
                case "light": return Presets["paper"];
                case "dark": return Presets["graphite"];
                case "custom":
                    if (a.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 5)
                    {
                        var colors = c.EnumerateArray().Select(v => v.TryGetUInt32(out var n) ? n & 0xFFFFFF : 0).ToArray();
                        foreach (var (id, legacy) in LegacyPresets)
                            if (legacy.SequenceEqual(colors)) return Presets[id];
                        var warn = Presets[IsDark(colors[0]) ? "graphite" : "paper"];
                        return new Roles(colors[0], colors[1], colors[2], colors[2], colors[2], colors[4],
                            DeriveRam(colors[4], colors[0]), warn.OverPace, warn.FarOver);
                    }
                    return Presets["midnight"];
                default: return system;
            }
        }
        catch (JsonException) { return system; }
    }

    private static Roles? ReadRoles(JsonElement r)
    {
        uint? Get(string name) => r.TryGetProperty(name, out var v) ? ParseColor(v) : null;
        if (Get("background") is not uint bg || Get("text") is not uint text || Get("session") is not uint session
            || Get("weekly") is not uint weekly || Get("fable") is not uint fable || Get("cpu") is not uint cpu
            || Get("over_pace") is not uint over || Get("far_over") is not uint far)
            return null;
        return new Roles(bg, text, session, weekly, fable, cpu, Get("ram") ?? DeriveRam(cpu, bg), over, far);
    }

    private static uint? ParseColor(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n)) return n & 0xFFFFFF;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()?.Trim().TrimStart('#');
        return s is { Length: 6 } && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : null;
    }

    private static uint DeriveRam(uint cpu, uint bg) => IsDark(bg) ? Mix(cpu, bg, 0.35) : Mix(cpu, 0x000000, 0.3);

    public static bool IsDark(uint rgb)
    {
        uint r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        return 299 * r + 587 * g + 114 * b < 128000;
    }

    public static uint Mix(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        uint Ch(int shift)
        {
            double x = (a >> shift) & 255, y = (b >> shift) & 255;
            return (uint)Math.Round(x + (y - x) * t) << shift;
        }
        return Ch(16) | Ch(8) | Ch(0);
    }

    public static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>WCAG contrast ratio between two colours.</summary>
    public static double Contrast(uint a, uint b)
    {
        static double Lum(uint rgb)
        {
            static double Lin(uint c) { double v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin((rgb >> 16) & 255) + 0.7152 * Lin((rgb >> 8) & 255) + 0.0722 * Lin(rgb & 255);
        }
        double x = Lum(a), y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Near-black or near-white, whichever reads better on `rgb`.</summary>
    private static uint InkOn(uint rgb) => Contrast(rgb, 0x111111) >= Contrast(rgb, 0xffffff) ? 0x111111u : 0xffffffu;

    /// <summary>
    /// The palette for a theme. Muted text, hairlines and tracks are worked out from the
    /// background and text as the monitor does, so they always stay visible against it.
    /// The accent is the 7d colour and the measuring colour the Fable one.
    /// </summary>
    public static Palette Build(Roles r)
    {
        bool dark = IsDark(r.Background);
        Color C(uint rgb) => ToColor(rgb);
        uint field = dark ? Mix(r.Background, 0x000000, 0.35) : Mix(r.Background, r.Text, 0.05);
        // A Fable colour too close to the accent would make a measure look like a selection.
        uint measure = new[] { r.Fable, r.Session, r.OverPace }.First(c => Contrast(c, r.Weekly) >= 1.4 || c == r.OverPace);
        return new Palette(
            dark,
            C(r.Background),
            C(r.Text),
            C(Mix(r.Background, r.Text, 0.82)),
            C(Mix(r.Background, r.Text, 0.62)),
            C(Mix(r.Background, r.Text, 0.45)),
            C(Mix(r.Background, r.Text, 0.16)),
            C(field),
            C(Mix(r.Background, r.Text, 0.28)),
            C(r.Weekly),
            C(InkOn(r.Weekly)),
            C(measure),
            C(InkOn(measure)),
            C(r.FarOver));
    }
}
