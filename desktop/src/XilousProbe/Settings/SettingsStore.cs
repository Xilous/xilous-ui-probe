using System.IO;
using System.Text.Json;

namespace XilousProbe.Settings;

/// <summary>Everything the app remembers, in %APPDATA%\XilousProbe\settings.json.</summary>
public sealed class SettingsData
{
    /// <summary>Where the widget docks: the point it snaps to the nearest outer edge from, in physical pixels.</summary>
    public double? WidgetX { get; set; }
    public double? WidgetY { get; set; }
    public bool WidgetHidden { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    public SettingsData Data { get; private set; } = new();

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XilousProbe");

    public static string LocalDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XilousProbe");

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDir, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            // Keys from earlier versions (the website box, the web pointer's preferences) are ignored.
            if (File.Exists(_path))
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file must not stop the app from starting. Keep it aside for inspection.
            File.Copy(_path, _path + ".broken", overwrite: true);
            Data = new();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
