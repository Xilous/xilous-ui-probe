using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XilousProbe.Web;

namespace XilousProbe.Settings;

/// <summary>
/// The extension's own settings page (settings/index.html), hosted in WebView2 with
/// chrome.storage backed by the app's settings file. One settings UI for both.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const string Host = "xilous-probe.local";
    private readonly SettingsStore _store;
    private readonly WebView2 _web = new();
    private readonly Action<IReadOnlyDictionary<string, JsonNode?>> _onPrefsChanged;

    public SettingsWindow(SettingsStore store)
    {
        _store = store;
        Title = "Xilous Probe settings";
        Width = 980;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon-32.png"));
        Content = _web;
        _onPrefsChanged = changes => Dispatcher.BeginInvoke(() => PushChanges(changes));
        _store.WebPrefsChanged += _onPrefsChanged;
        Closed += (_, _) => _store.WebPrefsChanged -= _onPrefsChanged;
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var site = WebAssets.ExtractSettingsSite(Path.Combine(SettingsStore.LocalDataDir, "web"));
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(SettingsStore.LocalDataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(Host, site, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebAssets.BuildSettingsBootstrap());
            core.WebMessageReceived += OnMessage;
            core.Navigate($"https://{Host}/settings/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Settings need the Microsoft Edge WebView2 Runtime, which this PC does not have.",
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own page talks to us.
        if (!e.Source.StartsWith($"https://{Host}/", StringComparison.Ordinal)) return;
        JsonObject? msg;
        try { msg = JsonNode.Parse(e.TryGetWebMessageAsString()) as JsonObject; }
        catch { return; }
        if (msg is null) return;
        int id = msg["id"]?.GetValue<int>() ?? 0;
        var payload = msg["payload"];
        var reply = new JsonObject { ["id"] = id };
        switch (msg["kind"]?.GetValue<string>())
        {
            case "storage.get":
                reply["result"] = _store.GetWebPrefs((payload?["keys"] as JsonArray)?.Select(k => k?.GetValue<string>() ?? "").ToList());
                break;
            case "storage.set":
                if (payload?["items"] is JsonObject items) _store.SetWebPrefs((JsonObject)items.DeepClone());
                break;
            case "storage.remove":
                _store.RemoveWebPrefs((payload?["keys"] as JsonArray)?.Select(k => k?.GetValue<string>() ?? "") ?? []);
                break;
            default:
                reply["error"] = "not available in settings";
                break;
        }
        if (id != 0) _web.CoreWebView2.PostWebMessageAsString(reply.ToJsonString());
    }

    private void PushChanges(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        if (_web.CoreWebView2 is null) return;
        var obj = new JsonObject();
        foreach (var (k, v) in changes) obj[k] = v?.DeepClone();
        _web.CoreWebView2.PostWebMessageAsString(new JsonObject { ["event"] = "storage", ["changes"] = obj }.ToJsonString());
    }
}
