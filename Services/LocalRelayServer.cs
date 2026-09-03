using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BitchatWin.Nostr;

namespace BitchatWin.Services;

/// <summary>
/// A minimal Nostr relay, enough to carry geohash channels between machines on
/// a local network with no internet at all.
///
/// It speaks the NIP-01 subset the client actually uses — REQ / EVENT / CLOSE,
/// with EOSE and OK — and verifies every event's id and signature before
/// accepting it, exactly as a public relay would.
///
/// The WebSocket handshake and framing are implemented over a raw TcpListener
/// on purpose: HttpListener needs an administrator-registered URL ACL to bind
/// anything other than localhost, which would make "just run it" impossible.
/// </summary>
public sealed class LocalRelayServer : IAsyncDisposable
{
    /// Events are ephemeral by kind, but a short history lets someone who joins
    /// a minute later still see the conversation.
    private const int MaxStoredEvents = 2000;

    private readonly object _storeGate = new();
    private readonly LinkedList<NostrEvent> _store = new();
    private readonly HashSet<string> _storedIds = new();

    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public event Action<string>? Log;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public int ClientCount => _clients.Count;

    public int StoredEventCount
    {
        get { lock (_storeGate) return _store.Count; }
    }

    /// <summary>Starts listening on all interfaces. Returns the chosen port.</summary>
    public int Start(int port = 8787)
    {
        if (IsRunning) return Port;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Log?.Invoke($"relay locale in ascolto su ws://0.0.0.0:{Port}");
        return Port;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener = null;

        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* shutting down */ }
        }

        _cts.Dispose();
        _cts = null;
        Log?.Invoke("relay locale fermato");
    }

    /// <summary>Every local IPv4 address a peer could reach this relay on.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    addresses.Add(ua.Address.ToString());
                }
            }
        }
        catch
        {
            // An enumeration failure just means we cannot suggest an address.
        }
        return addresses;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!token.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => ServeClientAsync(tcp, token), token);
        }
    }

    private async Task ServeClientAsync(TcpClient tcp, CancellationToken token)
    {
        var id = Guid.NewGuid();
        ClientConnection? client = null;

        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            if (!await PerformHandshakeAsync(stream, token).ConfigureAwait(false)) return;

            client = new ClientConnection(id, tcp, stream);
            _clients[id] = client;
            Log?.Invoke($"peer connesso ({_clients.Count} attivi)");

            while (!token.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, token).ConfigureAwait(false);
                if (frame is null) break;

                if (frame.Value.Opcode == 0x8) break;                 // close
                if (frame.Value.Opcode == 0x9)                        // ping
                {
                    await client.SendFrameAsync(0xA, frame.Value.Payload, token).ConfigureAwait(false);
                    continue;
                }
                if (frame.Value.Opcode != 0x1 && frame.Value.Opcode != 0x0) continue;

                string message = Encoding.UTF8.GetString(frame.Value.Payload);
                await HandleMessageAsync(client, message, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Peer went away; nothing to report.
        }
        catch (Exception ex)
        {
            Log?.Invoke("errore peer: " + ex.Message);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client?.Dispose();
            tcp.Dispose();
        }
    }

    // MARK: - Nostr protocol

    private async Task HandleMessageAsync(ClientConnection client, string raw, CancellationToken token)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException) { return; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;
            if (root[0].ValueKind != JsonValueKind.String) return;

            switch (root[0].GetString())
            {
                case "EVENT":
                    await HandleEventAsync(client, root, token).ConfigureAwait(false);
                    break;
                case "REQ":
                    await HandleReqAsync(client, root, token).ConfigureAwait(false);
                    break;
                case "CLOSE":
                    if (root[1].ValueKind == JsonValueKind.String)
                    {
                        client.RemoveSubscription(root[1].GetString() ?? string.Empty);
                    }
                    break;
            }
        }
    }

    private async Task HandleEventAsync(ClientConnection client, JsonElement root, CancellationToken token)
    {
        if (root.GetArrayLength() < 2) return;

        var nostrEvent = NostrEvent.FromJson(root[1]);
        if (nostrEvent is null)
        {
            await client.SendTextAsync("[\"NOTICE\",\"evento malformato\"]", token).ConfigureAwait(false);
            return;
        }

        // A relay that accepts unverified events lets anyone forge messages in
        // anyone's name, so this check is not optional even on a private LAN.
        if (!nostrEvent.VerifySignature())
        {
            await client.SendTextAsync(
                $"[\"OK\",{JsonSerializer.Serialize(nostrEvent.Id)},false,\"invalid: bad signature\"]",
                token).ConfigureAwait(false);
            return;
        }

        bool isNew = Store(nostrEvent);
        await client.SendTextAsync(
            $"[\"OK\",{JsonSerializer.Serialize(nostrEvent.Id)},true,{JsonSerializer.Serialize(isNew ? "" : "duplicate: have this event")}]",
            token).ConfigureAwait(false);

        if (!isNew) return;

        foreach (var peer in _clients.Values)
        {
            foreach (var (subId, filters) in peer.Subscriptions)
            {
                if (!filters.Any(f => f.Matches(nostrEvent))) continue;

                try
                {
                    await peer.SendTextAsync(
                        $"[\"EVENT\",{JsonSerializer.Serialize(subId)},{nostrEvent.ToWireJson()}]",
                        token).ConfigureAwait(false);
                }
                catch
                {
                    // A dead peer is cleaned up by its own serve loop.
                }
                break;
            }
        }
    }

    private async Task HandleReqAsync(ClientConnection client, JsonElement root, CancellationToken token)
    {
        if (root[1].ValueKind != JsonValueKind.String) return;
        string subId = root[1].GetString() ?? string.Empty;
        if (subId.Length == 0) return;

        var filters = new List<RelayFilter>();
        for (int i = 2; i < root.GetArrayLength(); i++)
        {
            var filter = RelayFilter.Parse(root[i]);
            if (filter is not null) filters.Add(filter);
        }
        if (filters.Count == 0) filters.Add(new RelayFilter());

        client.SetSubscription(subId, filters);

        // Replay history newest-first up to the smallest limit, then send in
        // chronological order so the conversation reads correctly on arrival.
        List<NostrEvent> snapshot;
        lock (_storeGate) snapshot = _store.ToList();

        int limit = filters.Min(f => f.Limit ?? 500);
        var matches = snapshot
            .Where(e => filters.Any(f => f.Matches(e)))
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .OrderBy(e => e.CreatedAt)
            .ToList();

        foreach (var stored in matches)
        {
            await client.SendTextAsync(
                $"[\"EVENT\",{JsonSerializer.Serialize(subId)},{stored.ToWireJson()}]",
                token).ConfigureAwait(false);
        }

        await client.SendTextAsync($"[\"EOSE\",{JsonSerializer.Serialize(subId)}]", token).ConfigureAwait(false);
    }

    /// <summary>Adds the event to the ring buffer. False when it was already held.</summary>
    private bool Store(NostrEvent nostrEvent)
    {
        lock (_storeGate)
        {
            if (!_storedIds.Add(nostrEvent.Id)) return false;

            _store.AddLast(nostrEvent);
            while (_store.Count > MaxStoredEvents)
            {
                var oldest = _store.First!.Value;
                _store.RemoveFirst();
                _storedIds.Remove(oldest.Id);
            }
            return true;
        }
    }

    // MARK: - WebSocket plumbing

    private static async Task<bool> PerformHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new MemoryStream();
        var buffer = new byte[1];

        // Read until the end of the HTTP headers, bounded so a peer cannot make
        // us buffer forever.
        while (header.Length < 8192)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return false;
            header.WriteByte(buffer[0]);

            if (header.Length >= 4)
            {
                var span = header.GetBuffer().AsSpan(0, (int)header.Length);
                if (span[^4] == '\r' && span[^3] == '\n' && span[^2] == '\r' && span[^1] == '\n') break;
            }
        }

        string request = Encoding.UTF8.GetString(header.GetBuffer(), 0, (int)header.Length);
        string? key = request
            .Split("\r\n")
            .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1]
            .Trim();

        if (string.IsNullOrEmpty(key)) return false;

        // RFC 6455: accept = base64(sha1(key + magic GUID))
        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), token).ConfigureAwait(false);
        return true;
    }

    private readonly struct Frame
    {
        public Frame(int opcode, byte[] payload)
        {
            Opcode = opcode;
            Payload = payload;
        }

        public int Opcode { get; }
        public byte[] Payload { get; }
    }

    private static async Task<Frame?> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var head = new byte[2];
        if (!await ReadExactAsync(stream, head, token).ConfigureAwait(false)) return null;

        int opcode = head[0] & 0x0F;
        bool masked = (head[1] & 0x80) != 0;
        long length = head[1] & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, token).ConfigureAwait(false)) return null;
            length = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, token).ConfigureAwait(false)) return null;
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        // One relay message is never megabytes; refuse anything that claims to be.
        if (length is < 0 or > 4 * 1024 * 1024) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, token).ConfigureAwait(false)) return null;

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, token).ConfigureAwait(false)) return null;

        if (masked)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
        }

        return new Frame(opcode, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>One connected peer: its socket, its send lock and its subscriptions.</summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, List<RelayFilter>> _subscriptions = new();

        public ClientConnection(Guid id, TcpClient tcp, NetworkStream stream)
        {
            Id = id;
            _tcp = tcp;
            _stream = stream;
        }

        public Guid Id { get; }

        public IEnumerable<(string SubId, List<RelayFilter> Filters)> Subscriptions =>
            _subscriptions.Select(kv => (kv.Key, kv.Value));

        public void SetSubscription(string subId, List<RelayFilter> filters) => _subscriptions[subId] = filters;

        public void RemoveSubscription(string subId) => _subscriptions.TryRemove(subId, out _);

        public Task SendTextAsync(string text, CancellationToken token) =>
            SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text), token);

        public async Task SendFrameAsync(int opcode, byte[] payload, CancellationToken token)
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var header = new List<byte> { (byte)(0x80 | opcode) };

                // Server-to-client frames are never masked.
                if (payload.Length < 126)
                {
                    header.Add((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    header.Add(126);
                    var ext = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(ext, (ushort)payload.Length);
                    header.AddRange(ext);
                }
                else
                {
                    header.Add(127);
                    var ext = new byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(ext, (ulong)payload.Length);
                    header.AddRange(ext);
                }

                await _stream.WriteAsync(header.ToArray(), token).ConfigureAwait(false);
                if (payload.Length > 0) await _stream.WriteAsync(payload, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            try { _stream.Dispose(); } catch { }
            try { _tcp.Dispose(); } catch { }
        }
    }
}

/// <summary>Server-side NIP-01 filter: parses a REQ filter and matches events against it.</summary>
public sealed class RelayFilter
{
    private List<string>? _ids;
    private List<string>? _authors;
    private List<int>? _kinds;
    private long? _since;
    private long? _until;
    private readonly Dictionary<string, List<string>> _tags = new();

    public int? Limit { get; private set; }

    public static RelayFilter? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var filter = new RelayFilter();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "ids": filter._ids = Strings(property.Value); break;
                case "authors": filter._authors = Strings(property.Value); break;
                case "kinds":
                    filter._kinds = property.Value.ValueKind == JsonValueKind.Array
                        ? property.Value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.Number)
                            .Select(v => v.GetInt32()).ToList()
                        : null;
                    break;
                case "since": filter._since = property.Value.TryGetInt64(out long s) ? s : null; break;
                case "until": filter._until = property.Value.TryGetInt64(out long u) ? u : null; break;
                case "limit": filter.Limit = property.Value.TryGetInt32(out int l) ? l : null; break;
                default:
                    // Tag filters are "#<single letter>".
                    if (property.Name.Length == 2 && property.Name[0] == '#')
                    {
                        var values = Strings(property.Value);
                        if (values is not null) filter._tags[property.Name[1].ToString()] = values;
                    }
                    break;
            }
        }
        return filter;
    }

    public bool Matches(NostrEvent nostrEvent)
    {
        if (_ids is not null && !_ids.Contains(nostrEvent.Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (_authors is not null && !_authors.Contains(nostrEvent.Pubkey, StringComparer.OrdinalIgnoreCase)) return false;
        if (_kinds is not null && !_kinds.Contains(nostrEvent.Kind)) return false;
        if (_since is not null && nostrEvent.CreatedAt < _since) return false;
        if (_until is not null && nostrEvent.CreatedAt > _until) return false;

        foreach (var (name, wanted) in _tags)
        {
            bool hit = nostrEvent.Tags.Any(t =>
                t.Count >= 2 && t[0] == name && wanted.Contains(t[1], StringComparer.OrdinalIgnoreCase));
            if (!hit) return false;
        }

        return true;
    }

    private static List<string>? Strings(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? string.Empty)
                .ToList()
            : null;
}
