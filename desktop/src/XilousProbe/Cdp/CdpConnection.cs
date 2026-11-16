using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XilousProbe.Cdp;

public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// One WebSocket to a browser's DevTools endpoint, with flattened sessions: every
/// command and event carries an optional sessionId, so one socket serves the
/// browser and every page attached through it.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _readLoop;

    /// <summary>An event: method, params, and the sessionId it came from (null for the browser).</summary>
    public event Action<string, JsonObject, string?>? EventReceived;
    public event Action? Closed;

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri endpoint, CancellationToken ct = default)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(endpoint, ct);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonObject> SendAsync(string method, object? parameters = null, string? sessionId = null,
        TimeSpan? timeout = null)
    {
        int id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject { ["id"] = id, ["method"] = method };
        msg["params"] = parameters switch
        {
            null => new JsonObject(),
            JsonObject o => o,
            _ => JsonSerializer.SerializeToNode(parameters),
        };
        if (sessionId is not null) msg["sessionId"] = sessionId;

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        await using var reg = timer.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException($"{method} timed out"));
        });
        return await tcs.Task;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                JsonObject? msg;
                try { msg = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length))?.AsObject(); }
                catch (JsonException) { continue; }
                if (msg is null) continue;
                Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            foreach (var (_, t) in _pending) t.TrySetException(new CdpException("The DevTools connection closed"));
            _pending.Clear();
            Closed?.Invoke();
        }
    }

    private void Dispatch(JsonObject msg)
    {
        if (msg["id"] is JsonValue idVal && idVal.TryGetValue(out int id))
        {
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (msg["error"] is JsonObject err)
                tcs.TrySetException(new CdpException(err["message"]?.GetValue<string>() ?? "CDP error"));
            else
                tcs.TrySetResult(msg["result"] as JsonObject ?? []);
            return;
        }
        var method = msg["method"]?.GetValue<string>();
        if (method is null) return;
        var sessionId = msg["sessionId"]?.GetValue<string>();
        var p = msg["params"] as JsonObject ?? [];
        try { EventReceived?.Invoke(method, p, sessionId); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CDP event handler failed for {method}: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* closing a socket the other side already dropped */ }
        if (_readLoop is not null) await Task.WhenAny(_readLoop, Task.Delay(2000));
        _ws.Dispose();
        _cts.Dispose();
    }
}
