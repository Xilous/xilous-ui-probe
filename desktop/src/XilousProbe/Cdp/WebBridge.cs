using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using XilousProbe.Capture;
using XilousProbe.Settings;
using XilousProbe.Web;

namespace XilousProbe.Cdp;

/// <summary>
/// Plays the part the extension's service worker played: storage, clipboard,
/// stylesheet fetches and the shader agent, for every page in every attached browser.
/// </summary>
public sealed class WebBridge : IAsyncDisposable
{
    // The same limits the extension's worker keeps (background.js).
    private const int MaxSheetFetch = 12;
    private const int MaxSheetBytes = 4 * 1024 * 1024;
    private static readonly HttpClient SheetHttp = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<int, CdpBrowser> _browsers = new();
    private bool _pointMode;

    public event Action<bool>? PointModeRequested;
    public event Action? SettingsRequested;
    public event Action<string>? Log;
    public event Action? BrowsersChanged;

    public WebBridge(SettingsStore settings)
    {
        _settings = settings;
        _settings.WebPrefsChanged += changes =>
        {
            var obj = new JsonObject();
            foreach (var (k, v) in changes) obj[k] = v?.DeepClone();
            _ = BroadcastAsync(new JsonObject { ["event"] = "storage", ["changes"] = obj });
        };
    }

    public IReadOnlyCollection<CdpBrowser> Browsers => _browsers.Values.ToList();

    public CdpBrowser? BrowserOnPort(int port) =>
        _browsers.TryGetValue(port, out var b) && b.IsOpen ? b : null;

    /// <summary>Whether a window's process is one the web pointer covers.</summary>
    /// <remarks>An app whose pages all refused the pointer is left to UI Automation.</remarks>
    public bool OwnsProcess(uint pid) => pid != 0 && _browsers.Values.Any(b =>
        b.IsOpen && b.ProcessId == pid && b.Pages.Any(p => !p.Blocked));

    public async Task<CdpBrowser> AttachAsync(string name, int port, TimeSpan wait)
    {
        if (BrowserOnPort(port) is { } existing) return existing;
        var browser = await CdpBrowser.AttachAsync(name, port, wait);
        browser.RequestReceived += OnRequestAsync;
        browser.Log += m => Log?.Invoke(m);
        browser.Disconnected += b =>
        {
            _browsers.TryRemove(b.Port, out _);
            Log?.Invoke($"{b.Name}: disconnected");
            BrowsersChanged?.Invoke();
        };
        _browsers[port] = browser;
        BrowsersChanged?.Invoke();
        return browser;
    }

    public Task SetPointModeAsync(bool on)
    {
        _pointMode = on;
        return BroadcastAsync(new JsonObject
        {
            ["event"] = "message",
            ["message"] = new JsonObject { ["type"] = "TOGGLE_PROBE", ["active"] = on },
        });
    }

    private Task BroadcastAsync(JsonObject message) =>
        Task.WhenAll(_browsers.Values.Where(b => b.IsOpen).Select(b => b.BroadcastAsync(message)));

    private async Task<JsonNode?> OnRequestAsync(PageRequest req)
    {
        switch (req.Kind)
        {
            case "ready":
                // A page that loaded (or reloaded) while pointing joins in.
                if (_pointMode)
                    await req.Browser.DeliverAsync(req.Page, new JsonObject
                    {
                        ["event"] = "message",
                        ["message"] = new JsonObject { ["type"] = "TOGGLE_PROBE", ["active"] = true },
                    });
                if (DeepShaderCapture)
                    await TryAsync(() => req.Browser.EvaluateAsync(req.Page, WebAssets.Read("web/shader-agent.js")));
                return null;

            case "blocked":
                return null;

            case "storage.get":
            {
                var keys = (req.Payload?["keys"] as JsonArray)?.Select(k => k?.GetValue<string>() ?? "").ToList();
                return _settings.GetWebPrefs(keys);
            }
            case "storage.set":
                if (req.Payload?["items"] is JsonObject items) _settings.SetWebPrefs((JsonObject)items.DeepClone());
                return null;
            case "storage.remove":
                _settings.RemoveWebPrefs((req.Payload?["keys"] as JsonArray)?.Select(k => k?.GetValue<string>() ?? "") ?? []);
                return null;

            // Only while pointing: the binding is the page's channel, and a page should
            // never be able to fill the clipboard on its own.
            case "clipboard.text" or "clipboard.png" when !_pointMode:
                throw new InvalidOperationException("Copying works while Point Mode is on");
            case "clipboard.text":
                await ClipboardService.SetTextAsync(req.Payload?["text"]?.GetValue<string>() ?? "");
                return null;
            case "clipboard.png":
                await ClipboardService.SetPngAsync(Convert.FromBase64String(req.Payload?["base64"]?.GetValue<string>() ?? ""));
                return null;

            case "runtime.message":
                return await OnRuntimeMessageAsync(req);
        }
        throw new InvalidOperationException($"Unknown request {req.Kind}");
    }

    private bool DeepShaderCapture =>
        _settings.GetWebPrefs(["editDeepShaderCapture"])["editDeepShaderCapture"] is JsonValue v &&
        v.TryGetValue(out bool on) && on;

    private async Task<JsonNode?> OnRuntimeMessageAsync(PageRequest req)
    {
        var type = req.Payload?["type"]?.GetValue<string>();
        switch (type)
        {
            case "DEACTIVATE":
                // Esc at the last layer in a page ends Point Mode everywhere, as the
                // toolbar button would.
                PointModeRequested?.Invoke(false);
                return null;
            case "OPEN_SETTINGS":
                SettingsRequested?.Invoke();
                return null;
            case "INJECT_SHADER_AGENT":
                try
                {
                    await req.Browser.EvaluateAsync(req.Page, WebAssets.Read("web/shader-agent.js"));
                    return new JsonObject { ["ok"] = true };
                }
                catch (Exception ex)
                {
                    return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
                }
            case "FETCH_STYLESHEETS":
            {
                var urls = (req.Payload?["urls"] as JsonArray)?.Select(u => u?.GetValue<string>()).OfType<string>().Take(MaxSheetFetch).ToList() ?? [];
                var sheets = await Task.WhenAll(urls.Select(FetchStylesheetAsync));
                return new JsonObject { ["sheets"] = new JsonArray(sheets) };
            }
        }
        return null;
    }

    /// <summary>A stylesheet the page could not read itself. No cookies, http(s) only, CSS only, 4 MB at most.</summary>
    public static async Task<JsonNode> FetchStylesheetAsync(string url)
    {
        var result = new JsonObject { ["url"] = url };
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                result["error"] = "unsupported scheme";
                return result;
            }
            using var res = await SheetHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) { result["error"] = $"HTTP {(int)res.StatusCode}"; return result; }
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && type is not ("text/css" or "text/plain" or "application/octet-stream"))
            {
                result["error"] = $"not a stylesheet ({type})";
                return result;
            }
            if (res.Content.Headers.ContentLength > MaxSheetBytes) { result["error"] = "too large"; return result; }
            var text = await res.Content.ReadAsStringAsync();
            if (text.Length > MaxSheetBytes) { result["error"] = "too large"; return result; }
            result["text"] = text;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }
        return result;
    }

    private static async Task TryAsync(Func<Task> work)
    {
        try { await work(); } catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _browsers.Values) await b.DisposeAsync();
        _browsers.Clear();
    }
}
