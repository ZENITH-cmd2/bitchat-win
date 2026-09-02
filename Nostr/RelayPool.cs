using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BitchatWin.Nostr;

/// <summary>
/// A set of relay connections addressed as one. Subscriptions and publishes fan
/// out to every relay in the set; inbound events are verified and de-duplicated
/// so the same message arriving on five relays surfaces once.
/// </summary>
public sealed class RelayPool : IAsyncDisposable
{
    /// <summary>Verified, de-duplicated events. Raised on a background thread.</summary>
    public event Action<NostrEvent>? EventReceived;

    /// <summary>Per-relay connection state, for the status bar.</summary>
    public event Action<string, bool, string?>? RelayStatusChanged;

    /// <summary>
    /// A relay's verdict on something we published: relay URL, event id, whether
    /// it was accepted, and the relay's reason. Without this a rejected message
    /// would look identical to a delivered one.
    /// </summary>
    public event Action<string, string, bool, string>? PublishAck;

    /// <summary>A relay NOTICE, which is where relays explain themselves.</summary>
    public event Action<string, string>? Notice;

    private readonly ConcurrentDictionary<string, RelayConnection> _connections = new();
    private readonly ConcurrentDictionary<string, NostrFilter> _subscriptions = new();

    private readonly object _seenGate = new();
    private readonly HashSet<string> _seenIds = new();
    private readonly Queue<string> _seenOrder = new();
    private const int SeenCapacity = 8000;

    public IReadOnlyCollection<string> Relays => _connections.Keys.ToList();

    public int ConnectedCount => _connections.Values.Count(c => c.IsConnected);

    /// <summary>
    /// Makes the live relay set match <paramref name="urls"/>: opens what is
    /// new, drops what is gone, leaves the rest untouched.
    /// </summary>
    public void SetRelays(IEnumerable<string> urls)
    {
        var target = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in _connections.Keys.ToList())
        {
            if (target.Contains(existing)) continue;
            if (_connections.TryRemove(existing, out var connection))
            {
                RelayStatusChanged?.Invoke(existing, false, "removed");
                _ = connection.DisposeAsync();
            }
        }

        foreach (string url in target)
        {
            if (_connections.ContainsKey(url)) continue;

            var connection = new RelayConnection(url, HandleRelayMessage, OnRelayStatus);
            if (_connections.TryAdd(url, connection))
            {
                foreach (var pair in _subscriptions) connection.Send(BuildReq(pair.Key, pair.Value));
                connection.Start();
            }
            else
            {
                _ = connection.DisposeAsync();
            }
        }
    }

    public void Subscribe(string subscriptionId, NostrFilter filter)
    {
        _subscriptions[subscriptionId] = filter;
        string req = BuildReq(subscriptionId, filter);
        foreach (var connection in _connections.Values) connection.Send(req);
    }

    public void Unsubscribe(string subscriptionId)
    {
        if (!_subscriptions.TryRemove(subscriptionId, out _)) return;

        string close = "[\"CLOSE\"," + JsonSerializer.Serialize(subscriptionId) + "]";
        foreach (var connection in _connections.Values) connection.Send(close);
    }

    public void Publish(NostrEvent nostrEvent)
    {
        string message = "[\"EVENT\"," + nostrEvent.ToWireJson() + "]";
        foreach (var connection in _connections.Values) connection.Send(message);
    }

    private void OnRelayStatus(string url, bool connected, string? detail) =>
        RelayStatusChanged?.Invoke(url, connected, detail);

    private void HandleRelayMessage(string url, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            string? type = root[0].GetString();

            if (type == "OK" && root.GetArrayLength() >= 3)
            {
                string eventId = root[1].GetString() ?? string.Empty;
                bool accepted = root[2].ValueKind == JsonValueKind.True;
                string reason = root.GetArrayLength() >= 4 ? root[3].GetString() ?? string.Empty : string.Empty;
                PublishAck?.Invoke(url, eventId, accepted, reason);
                return;
            }

            if (type == "NOTICE")
            {
                Notice?.Invoke(url, root[1].GetString() ?? string.Empty);
                return;
            }

            if (type != "EVENT" || root.GetArrayLength() < 3) return;

            var nostrEvent = NostrEvent.FromJson(root[2]);
            if (nostrEvent is null) return;

            // Relay output is untrusted: a bad signature means someone is
            // impersonating an author, so it never reaches the UI.
            if (!nostrEvent.VerifySignature()) return;

            if (!MarkSeen(nostrEvent.Id)) return;

            EventReceived?.Invoke(nostrEvent);
        }
        catch (JsonException)
        {
            // Malformed relay frame; nothing to recover.
        }
    }

    /// <summary>False when this event id has already been surfaced.</summary>
    private bool MarkSeen(string id)
    {
        lock (_seenGate)
        {
            if (!_seenIds.Add(id)) return false;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > SeenCapacity) _seenIds.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private static string BuildReq(string subscriptionId, NostrFilter filter)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("REQ");
            writer.WriteStringValue(subscriptionId);
            filter.WriteTo(writer);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values) await connection.DisposeAsync();
        _connections.Clear();
    }
}

/// <summary>
/// One relay socket, with a send queue and reconnect-with-backoff. Subscriptions
/// are replayed on every (re)connect, which is what makes a dropped relay heal
/// without the caller noticing.
/// </summary>
internal sealed class RelayConnection : IAsyncDisposable
{
    private readonly string _url;
    private readonly Action<string, string> _onMessage;
    private readonly Action<string, bool, string?> _onStatus;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly object _stateGate = new();
    private readonly Dictionary<string, string> _activeRequests = new();

    private Task? _worker;

    public bool IsConnected { get; private set; }

    public RelayConnection(string url, Action<string, string> onMessage, Action<string, bool, string?> onStatus)
    {
        _url = url;
        _onMessage = onMessage;
        _onStatus = onStatus;
    }

    public void Start() => _worker ??= Task.Run(RunAsync);

    public void Send(string message)
    {
        RememberSubscriptionState(message);
        _outbox.Writer.TryWrite(message);
    }

    /// <summary>Tracks REQ/CLOSE so a reconnect can restore the subscription set.</summary>
    private void RememberSubscriptionState(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            if (root[0].ValueKind != JsonValueKind.String) return;
            string? type = root[0].GetString();
            if (type != "REQ" && type != "CLOSE") return;

            // Only REQ/CLOSE carry a subscription id here. An EVENT frame holds
            // the event object in this slot, and reading it as a string throws.
            if (root[1].ValueKind != JsonValueKind.String) return;
            string? id = root[1].GetString();
            if (id is null) return;

            lock (_stateGate)
            {
                if (type == "REQ") _activeRequests[id] = message;
                else _activeRequests.Remove(id);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not a framed message we track; never let bookkeeping break a send.
        }
    }

    private async Task RunAsync()
    {
        int attempt = 0;

        while (!_cts.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(new Uri(_url), connectCts.Token).ConfigureAwait(false);

                attempt = 0;
                IsConnected = true;
                _onStatus(_url, true, null);

                // Replay subscriptions before anything queued behind them.
                List<string> replay;
                lock (_stateGate) replay = _activeRequests.Values.ToList();
                foreach (string request in replay) await SendRawAsync(socket, request).ConfigureAwait(false);

                var sendLoop = SendLoopAsync(socket, _cts.Token);
                await ReceiveLoopAsync(socket, _cts.Token).ConfigureAwait(false);
                await sendLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _onStatus(_url, false, ex.Message);
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    _onStatus(_url, false, "disconnected");
                }
            }

            if (_cts.IsCancellationRequested) break;

            // Backoff with jitter so a relay outage does not turn into a
            // synchronised reconnect storm from every client at once.
            attempt = Math.Min(attempt + 1, 6);
            int delayMs = (int)(Math.Pow(2, attempt) * 500) + Random.Shared.Next(0, 1000);
            try
            {
                await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out string? message))
                {
                    if (socket.State != WebSocketState.Open) return;
                    await SendRawAsync(socket, message).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (WebSocketException)
        {
            // The receive loop reports and the outer loop reconnects.
        }
    }

    private static Task SendRawAsync(ClientWebSocket socket, string message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var accumulator = new MemoryStream();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;

            accumulator.Write(buffer, 0, result.Count);
            // A relay that never sets EndOfMessage must not grow this forever.
            if (accumulator.Length > 2 * 1024 * 1024)
            {
                accumulator.SetLength(0);
                continue;
            }

            if (!result.EndOfMessage) continue;

            string message = Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
            accumulator.SetLength(0);
            _onMessage(_url, message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the process is going down anyway.
            }
        }
        _cts.Dispose();
    }
}
