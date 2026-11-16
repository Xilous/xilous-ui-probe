using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using XilousProbe.Web;

namespace XilousProbe.Cdp;

/// <summary>A page the app has attached to and injected the web pointer into.</summary>
public sealed class CdpPage
{
    public required string SessionId { get; init; }
    public required string TargetId { get; init; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The execution context the pointer last reported ready from; replies go there.</summary>
    public int? ContextId { get; set; }
    /// <summary>The page refused the web pointer (Trusted Types); it is pointed at through UI Automation.</summary>
    public bool Blocked { get; set; }
}

/// <summary>What a page asked the app for, and the page it came from.</summary>
public sealed record PageRequest(CdpBrowser Browser, CdpPage Page, int Id, string Kind, JsonNode? Payload);

/// <summary>
/// One browser (the probe Chrome, or an Electron app) reached over its DevTools port.
/// It auto-attaches to every page, injects the web pointer before the page's own
/// scripts run, and relays what the pointer asks for to <see cref="RequestReceived"/>.
/// </summary>
public sealed class CdpBrowser : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly CdpConnection _conn = new();
    private readonly ConcurrentDictionary<string, CdpPage> _pages = new();
    private readonly string _binding = "__xp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private readonly string _receiver = "__xp_rx_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private string? _bootstrap;

    public string Name { get; }
    public int Port { get; }
    /// <summary>The browser process, which owns every top-level window of the app.</summary>
    public uint ProcessId { get; private set; }
    public IReadOnlyCollection<CdpPage> Pages => _pages.Values.ToList();
    public bool IsOpen => _conn.IsOpen;

    public event Func<PageRequest, Task<JsonNode?>>? RequestReceived;
    public event Action<CdpBrowser>? Disconnected;
    public event Action<string>? Log;

    private CdpBrowser(string name, int port)
    {
        Name = name;
        Port = port;
    }

    /// <summary>Whether something answers DevTools' version endpoint on this port.</summary>
    public static async Task<JsonObject?> ProbeAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var text = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Waits for a DevTools endpoint to come up on the port, then attaches.</summary>
    public static async Task<CdpBrowser> AttachAsync(string name, int port, TimeSpan wait, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + wait;
        JsonObject? version;
        while ((version = await ProbeAsync(port, ct)) is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new CdpException($"Nothing answered DevTools on port {port}. Is the app running with remote debugging on that port?");
            await Task.Delay(400, ct);
        }
        var ws = version["webSocketDebuggerUrl"]?.GetValue<string>()
                 ?? throw new CdpException($"Port {port} answered, but without a DevTools WebSocket");

        var browser = new CdpBrowser(name, port);
        await browser.StartAsync(new Uri(ws), ct);
        return browser;
    }

    private async Task StartAsync(Uri ws, CancellationToken ct)
    {
        _bootstrap = WebAssets.BuildPageBootstrap(_binding, _receiver);
        _conn.EventReceived += OnEvent;
        _conn.Closed += () => Disconnected?.Invoke(this);
        await _conn.ConnectAsync(ws, ct);
        ProcessId = PortOwner.FindListeningProcess(Port) ?? 0;

        await _conn.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true });
        // Browser-level auto-attach covers the pages already open as well as new ones,
        // and holds a new page until it is released, so the pointer is in place before
        // the page's own scripts run.
        await _conn.SendAsync("Target.setAutoAttach", new JsonObject
        {
            ["autoAttach"] = true,
            ["waitForDebuggerOnStart"] = true,
            ["flatten"] = true,
            ["filter"] = new JsonArray(new JsonObject { ["type"] = "page" }),
        });
    }

    private void OnEvent(string method, JsonObject p, string? sessionId)
    {
        switch (method)
        {
            case "Target.attachedToTarget":
                _ = OnAttachedAsync(p);
                break;
            case "Target.detachedFromTarget":
                if (p["sessionId"]?.GetValue<string>() is { } sid) _pages.TryRemove(sid, out _);
                break;
            case "Target.targetInfoChanged":
                if (p["targetInfo"] is JsonObject info)
                {
                    var tid = info["targetId"]?.GetValue<string>();
                    foreach (var page in _pages.Values.Where(x => x.TargetId == tid))
                    {
                        page.Url = info["url"]?.GetValue<string>() ?? page.Url;
                        page.Title = info["title"]?.GetValue<string>() ?? page.Title;
                    }
                }
                break;
            case "Runtime.bindingCalled":
                if (sessionId is not null && p["name"]?.GetValue<string>() == _binding)
                    _ = OnBindingAsync(sessionId, p);
                break;
            case "Page.loadEventFired":
                // A page caught mid-navigation when we attached can lose the pointer with the
                // document it was injected into. The bootstrap is idempotent, so make sure.
                if (sessionId is not null && _pages.TryGetValue(sessionId, out var loaded))
                    _ = InjectAsync(loaded);
                break;
            case "Runtime.exceptionThrown":
                // The bootstrap on a new document runs where nothing reports back but this event.
                if (p["exceptionDetails"] is JsonObject thrown && ExceptionText(thrown) is var text &&
                    (text.Contains("ccp", StringComparison.OrdinalIgnoreCase) || text.Contains("xilous", StringComparison.OrdinalIgnoreCase) ||
                     (thrown["url"]?.GetValue<string>() ?? "").Length == 0))
                    Log?.Invoke($"{Name}: page script error: {text.Split('\n')[0]}");
                break;
            case "Runtime.executionContextsCleared":
                if (sessionId is not null && _pages.TryGetValue(sessionId, out var cleared)) cleared.ContextId = null;
                break;
        }
    }

    private async Task OnAttachedAsync(JsonObject p)
    {
        var sessionId = p["sessionId"]!.GetValue<string>();
        var info = p["targetInfo"] as JsonObject ?? [];
        bool waiting = p["waitingForDebugger"]?.GetValue<bool>() ?? false;
        var type = info["type"]?.GetValue<string>();
        if (type != "page")
        {
            if (waiting) await TrySend("Runtime.runIfWaitingForDebugger", null, sessionId);
            return;
        }

        var page = new CdpPage
        {
            SessionId = sessionId,
            TargetId = info["targetId"]?.GetValue<string>() ?? "",
            Url = info["url"]?.GetValue<string>() ?? "",
            Title = info["title"]?.GetValue<string>() ?? "",
        };
        _pages[sessionId] = page;

        try
        {
            await _conn.SendAsync("Runtime.addBinding", new JsonObject { ["name"] = _binding }, sessionId);
            await _conn.SendAsync("Runtime.enable", null, sessionId);
            await _conn.SendAsync("Page.enable", null, sessionId);
            // Strict-CSP sites would otherwise refuse the injected <style> elements.
            await _conn.SendAsync("Page.setBypassCSP", new JsonObject { ["enabled"] = true }, sessionId);
            await _conn.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new JsonObject { ["source"] = _bootstrap }, sessionId);
            if (waiting)
            {
                await _conn.SendAsync("Runtime.runIfWaitingForDebugger", null, sessionId);
            }
            else
            {
                // Already loaded before we attached: inject into the live document now.
                await InjectAsync(page);
            }
            Log?.Invoke($"{Name}: attached to {Describe(page)}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{Name}: could not prepare {Describe(page)}: {ex.Message}");
            if (waiting) await TrySend("Runtime.runIfWaitingForDebugger", null, sessionId);
        }
    }

    /// <summary>Runs the bootstrap in the page's current document. A no-op where the pointer is already running.</summary>
    private async Task InjectAsync(CdpPage page)
    {
        try
        {
            var result = await _conn.SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = _bootstrap,
                ["includeCommandLineAPI"] = false,
                ["silent"] = true,
            }, page.SessionId, TimeSpan.FromSeconds(30));
            if (result["exceptionDetails"] is JsonObject ex)
                Log?.Invoke($"{Name}: the pointer failed to start in {Describe(page)}: {ExceptionText(ex)}");
        }
        catch (Exception ex) when (ex is CdpException or TimeoutException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            Log?.Invoke($"{Name}: could not inject into {Describe(page)}: {ex.Message}");
        }
    }

    private async Task OnBindingAsync(string sessionId, JsonObject p)
    {
        if (!_pages.TryGetValue(sessionId, out var page)) return;
        int? contextId = p["executionContextId"]?.GetValue<int>();
        JsonObject? msg;
        try { msg = JsonNode.Parse(p["payload"]?.GetValue<string>() ?? "") as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (msg is null) return;

        int id = msg["id"]?.GetValue<int>() ?? 0;
        var kind = msg["kind"]?.GetValue<string>() ?? "";
        var payload = msg["payload"];
        if (kind is "ready" or "blocked")
        {
            page.ContextId = contextId;
            page.Url = payload?["url"]?.GetValue<string>() ?? page.Url;
            page.Blocked = kind == "blocked";
            if (page.Blocked)
                Log?.Invoke($"{Name}: {Describe(page)} enforces {payload?["reason"]?.GetValue<string>()}; pointing there uses UI Automation");
        }

        JsonObject reply = new() { ["id"] = id };
        try
        {
            var handler = RequestReceived;
            var result = handler is null ? null : await handler(new PageRequest(this, page, id, kind, payload));
            reply["result"] = result;
        }
        catch (Exception ex)
        {
            reply["error"] = ex.Message;
        }
        if (id != 0) await DeliverAsync(page, reply, contextId);
    }

    /// <summary>Sends a message to the pointer in a page (a reply, or an event such as TOGGLE_PROBE).</summary>
    public Task DeliverAsync(CdpPage page, JsonObject message, int? contextId = null)
    {
        var expr = $"(function(){{ var r = window[{WebAssets.Js(_receiver)}]; if (typeof r === 'function') r({WebAssets.Js(message.ToJsonString())}); }})()";
        var args = new JsonObject { ["expression"] = expr, ["silent"] = true };
        var ctx = contextId ?? page.ContextId;
        if (ctx is not null) args["contextId"] = ctx;
        return TrySend("Runtime.evaluate", args, page.SessionId);
    }

    public Task BroadcastAsync(JsonObject message) =>
        Task.WhenAll(_pages.Values.Select(page => DeliverAsync(page, message)));

    /// <summary>Runs a script in a page's main world, such as the shader agent on demand.</summary>
    public async Task EvaluateAsync(CdpPage page, string expression)
    {
        var args = new JsonObject { ["expression"] = expression, ["silent"] = true };
        if (page.ContextId is not null) args["contextId"] = page.ContextId;
        var result = await _conn.SendAsync("Runtime.evaluate", args, page.SessionId, TimeSpan.FromSeconds(30));
        if (result["exceptionDetails"] is JsonObject ex)
            throw new CdpException(ExceptionText(ex));
    }

    private static string ExceptionText(JsonObject details) =>
        details["exception"]?["description"]?.GetValue<string>() ?? details["text"]?.GetValue<string>() ?? "script failed";

    public async Task OpenTabAsync(string url)
    {
        await _conn.SendAsync("Target.createTarget", new JsonObject { ["url"] = url });
    }

    private async Task TrySend(string method, JsonObject? args, string? sessionId)
    {
        try { await _conn.SendAsync(method, args, sessionId); }
        catch (Exception ex) when (ex is CdpException or TimeoutException or System.Net.WebSockets.WebSocketException or ObjectDisposedException) { }
    }

    private static string Describe(CdpPage page) =>
        string.IsNullOrEmpty(page.Url) ? page.TargetId : page.Url;

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
