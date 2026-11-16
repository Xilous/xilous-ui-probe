using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XilousProbe.Settings;

/// <summary>How an app in the Apps list is started.</summary>
public enum AppKind
{
    /// <summary>A command run in a working directory, such as an electron-vite dev server.</summary>
    DevCommand,
    /// <summary>A packaged executable, launched with --remote-debugging-port appended.</summary>
    Executable,
    /// <summary>An app already running with remote debugging on a known port.</summary>
    Attach,
}

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public AppKind Kind { get; set; }
    /// <summary>The command line (DevCommand) or the exe path (Executable). {port} is replaced with the debugging port.</summary>
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int Port { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => Kind switch
    {
        AppKind.Attach => $"attach to port {Port}",
        _ => $"{Command} {Arguments}".Replace("{port}", Port.ToString()).Trim() +
             (string.IsNullOrEmpty(WorkingDirectory) ? "" : $"  ·  {WorkingDirectory}"),
    };
}

/// <summary>
/// Everything the app remembers, in %APPDATA%\XilousProbe\settings.json.
/// <see cref="WebPrefs"/> is the extension's chrome.storage.local, kept as raw JSON so the
/// web pointer and the settings page keep owning their own keys and defaults.
/// </summary>
public sealed class SettingsData
{
    public string LastUrl { get; set; } = "";
    public bool ChromeProfileIntroShown { get; set; }
    public List<AppEntry> Apps { get; set; } = [];
    public JsonObject WebPrefs { get; set; } = [];
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    public SettingsData Data { get; private set; } = new();

    /// <summary>Raised after web prefs change, with the keys and their new values (null when removed).</summary>
    public event Action<IReadOnlyDictionary<string, JsonNode?>>? WebPrefsChanged;

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
            if (File.Exists(_path))
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file must not stop the app from starting. Keep it aside for inspection.
            File.Copy(_path, _path + ".broken", overwrite: true);
            Data = new();
        }
        if (Data.Apps.Count == 0) Data.Apps.AddRange(DefaultApps());
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

    /// <summary>chrome.storage.local.get: null or empty keys means everything.</summary>
    public JsonObject GetWebPrefs(IEnumerable<string>? keys)
    {
        lock (_gate)
        {
            var result = new JsonObject();
            var wanted = keys?.ToList();
            foreach (var (k, v) in Data.WebPrefs)
                if (wanted is null || wanted.Count == 0 || wanted.Contains(k))
                    result[k] = v?.DeepClone();
            return result;
        }
    }

    /// <summary>chrome.storage.local.set. Raises <see cref="WebPrefsChanged"/> for keys whose value actually changed.</summary>
    public void SetWebPrefs(JsonObject items)
    {
        var changed = new Dictionary<string, JsonNode?>();
        lock (_gate)
        {
            foreach (var (k, v) in items)
            {
                if (Data.WebPrefs.TryGetPropertyValue(k, out var old) && JsonNode.DeepEquals(old, v)) continue;
                Data.WebPrefs[k] = v?.DeepClone();
                changed[k] = v?.DeepClone();
            }
        }
        if (changed.Count == 0) return;
        Save();
        WebPrefsChanged?.Invoke(changed);
    }

    public void RemoveWebPrefs(IEnumerable<string> keys)
    {
        var changed = new Dictionary<string, JsonNode?>();
        lock (_gate)
        {
            foreach (var k in keys)
                if (Data.WebPrefs.Remove(k)) changed[k] = null;
        }
        if (changed.Count == 0) return;
        Save();
        WebPrefsChanged?.Invoke(changed);
    }

    public static IEnumerable<AppEntry> DefaultApps()
    {
        var fieldwire = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "fieldwire_client_ucsh", "desktop");
        if (Directory.Exists(fieldwire))
        {
            yield return new AppEntry
            {
                Name = "Fieldwire client (dev)",
                Kind = AppKind.DevCommand,
                Command = "npm",
                Arguments = "run dev -- --remoteDebuggingPort {port}",
                WorkingDirectory = fieldwire,
                Port = 9333,
            };
        }
    }
}
