using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BitchatWin.Nostr;
using BitchatWin.Protocol;

namespace BitchatWin.Services;

/// <summary>Geohash precision levels, named as bitchat names them.</summary>
public enum GeohashLevel
{
    Region = 2,
    Province = 4,
    City = 5,
    Neighborhood = 6,
    Block = 7
}

public sealed record ChatMessage(
    string Id,
    string SenderPubkey,
    string DisplayName,
    string Content,
    DateTimeOffset Timestamp,
    bool IsMine);

public sealed record Participant(string Pubkey, string DisplayName, DateTimeOffset LastSeen);

/// <summary>
/// One bitchat location channel: derives the channel identity, picks the relays,
/// subscribes, publishes chat and presence.
///
/// Everything here is chosen to match the iOS/Android clients — relay set,
/// event kinds, tag layout, display-name convention — because a channel only
/// works if every participant agrees on all of it.
/// </summary>
public sealed class GeohashChannelService : IAsyncDisposable
{
    /// Presence heartbeat cadence, mirroring the Swift client.
    private static readonly TimeSpan PresenceMin = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan PresenceMax = TimeSpan.FromSeconds(80);

    /// A participant last heard from longer ago than this is dropped from the list.
    private static readonly TimeSpan ParticipantTtl = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Presence is announced only at coarse precisions. Announcing at block or
    /// neighbourhood level would tell the relays where someone physically is,
    /// so bitchat stays silent there and so does this client.
    /// </summary>
    private static readonly HashSet<int> PresencePrecisions = new() { 2, 4, 5 };

    private const int RelayCount = 5;

    public event Action<ChatMessage>? MessageReceived;
    public event Action? ParticipantsChanged;
    public event Action<int, int>? RelayStatusChanged;
    public event Action<string>? Log;

    /// <summary>Something the user needs to see in the conversation, not just the log.</summary>
    public event Action<string>? SystemNotice;

    private readonly RelayPool _pool = new();
    private readonly IdentityStore _identityStore;
    private readonly byte[] _deviceSeed;

    private readonly object _gate = new();
    private readonly Dictionary<string, Participant> _participants = new();
    private readonly HashSet<string> _displayedIds = new();
    private readonly Dictionary<string, SendState> _pendingSends = new();

    private CancellationTokenSource? _presenceCts;
    private string? _subscriptionId;

    public string? CurrentGeohash { get; private set; }
    public NostrIdentity? CurrentIdentity { get; private set; }
    public string Nickname { get; set; } = "anon";

    public GeohashChannelService(IdentityStore identityStore)
    {
        _identityStore = identityStore;
        _deviceSeed = _identityStore.GetOrCreateDeviceSeed();

        _pool.EventReceived += OnEventReceived;
        _pool.RelayStatusChanged += (url, connected, detail) =>
        {
            Log?.Invoke(connected ? $"connected  {url}" : $"offline    {url}{(detail is null ? "" : "  (" + detail + ")")}");
            RelayStatusChanged?.Invoke(_pool.ConnectedCount, _pool.Relays.Count);
        };

        _pool.Notice += (url, message) => Log?.Invoke($"notice     {url}  {message}");
        _pool.PublishAck += OnPublishAck;
    }

    /// <summary>
    /// Records each relay's verdict on a sent message. A message is delivered if
    /// any relay took it; only a clean sweep of rejections is worth interrupting
    /// the user about.
    /// </summary>
    private void OnPublishAck(string url, string eventId, bool accepted, string reason)
    {
        Log?.Invoke($"{(accepted ? "accepted  " : "rejected  ")} {url}{(reason.Length == 0 ? "" : "  " + reason)}");

        lock (_gate)
        {
            if (!_pendingSends.TryGetValue(eventId, out var state)) return;

            if (accepted) state.Accepted++;
            else
            {
                state.Rejected++;
                state.LastReason = reason;
            }

            _pendingSends[eventId] = state;

            // Every relay in the set has answered and none took it.
            if (state.Accepted == 0 && state.Rejected >= _pool.Relays.Count)
            {
                _pendingSends.Remove(eventId);
                SystemNotice?.Invoke($"messaggio rifiutato da tutti i relay{(state.LastReason.Length == 0 ? "" : ": " + state.LastReason)}");
            }
            else if (state.Accepted > 0)
            {
                _pendingSends.Remove(eventId);
            }
        }
    }

    private struct SendState
    {
        public int Accepted;
        public int Rejected;
        public string LastReason;
    }

    /// <summary>Your own display name in the current channel, e.g. <c>anon#1a2b</c>.</summary>
    public string MyDisplayName =>
        CurrentIdentity is null ? Nickname : FormatDisplayName(Nickname, CurrentIdentity.PublicKeyHex);

    public IReadOnlyList<Participant> Participants
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow - ParticipantTtl;
            lock (_gate)
            {
                return _participants.Values
                    .Where(p => p.LastSeen >= cutoff)
                    .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>Leaves any current channel and joins <paramref name="geohash"/>.</summary>
    public async Task JoinAsync(string geohash)
    {
        geohash = geohash.Trim().ToLowerInvariant();
        if (!Geohash.IsValid(geohash)) throw new ArgumentException($"'{geohash}' is not a valid geohash", nameof(geohash));

        await LeaveAsync().ConfigureAwait(false);

        CurrentGeohash = geohash;
        CurrentIdentity = NostrIdentity.DeriveForGeohash(_deviceSeed, geohash);

        var relays = GeoRelayDirectory.Shared.ClosestRelays(geohash, RelayCount);
        if (relays.Count == 0) throw new InvalidOperationException("Relay directory is empty");

        var (lat, lon) = Geohash.DecodeCenter(geohash);
        Log?.Invoke($"joining #{geohash}  (~{lat:F3}, {lon:F3})  as {MyDisplayName}");
        foreach (string relay in relays) Log?.Invoke($"relay      {relay}");

        _pool.SetRelays(relays);

        // Ephemeral events are not required to be stored, but relays that do
        // keep them give a new joiner some context instead of an empty room.
        _subscriptionId = "geo-" + Guid.NewGuid().ToString("n")[..12];
        _pool.Subscribe(_subscriptionId, NostrFilter.GeohashEphemeral(
            geohash,
            since: DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            limit: 200));

        StartPresenceLoop(geohash);
    }

    public async Task LeaveAsync()
    {
        if (_presenceCts is not null)
        {
            await _presenceCts.CancelAsync().ConfigureAwait(false);
            _presenceCts.Dispose();
            _presenceCts = null;
        }

        if (_subscriptionId is not null)
        {
            _pool.Unsubscribe(_subscriptionId);
            _subscriptionId = null;
        }

        lock (_gate)
        {
            _participants.Clear();
            _displayedIds.Clear();
        }
        ParticipantsChanged?.Invoke();

        CurrentGeohash = null;
        CurrentIdentity = null;
    }

    /// <summary>Signs and publishes a chat message to the current channel.</summary>
    public async Task SendMessageAsync(string text)
    {
        string content = text.Trim();
        if (content.Length == 0) return;

        string? geohash = CurrentGeohash;
        var identity = CurrentIdentity;
        if (geohash is null || identity is null) throw new InvalidOperationException("Not in a channel");

        var tags = new List<List<string>>
        {
            new() { "g", geohash },
            new() { "n", Nickname }
        };

        // created_at is fixed before mining: the nonce commits to the whole
        // serialised event, so the signed event must reuse this exact value.
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var nonceTag = await Task.Run(() => NostrPoW.MineNonceTag(
            identity.PublicKeyHex, createdAt, NostrKind.Ephemeral, tags, content)).ConfigureAwait(false);
        if (nonceTag is not null) tags.Add(nonceTag);

        var nostrEvent = new NostrEvent
        {
            Pubkey = identity.PublicKeyHex,
            CreatedAt = createdAt,
            Kind = NostrKind.Ephemeral,
            Tags = tags,
            Content = content
        }.Sign(identity.PrivateKey);

        bool isNew;
        lock (_gate)
        {
            isNew = _displayedIds.Add(nostrEvent.Id);
            _pendingSends[nostrEvent.Id] = new SendState { LastReason = string.Empty };
        }

        Log?.Invoke($"sending    {nostrEvent.Id[..12]}…  pow {NostrPoW.DifficultyOf(nostrEvent.Id)} bit  → {_pool.Relays.Count} relay");
        _pool.Publish(nostrEvent);

        if (isNew)
        {
            MessageReceived?.Invoke(new ChatMessage(
                nostrEvent.Id,
                identity.PublicKeyHex,
                MyDisplayName,
                content,
                DateTimeOffset.FromUnixTimeSeconds(createdAt),
                IsMine: true));
        }
    }

    private void OnEventReceived(NostrEvent nostrEvent)
    {
        string? geohash = CurrentGeohash;
        if (geohash is null) return;

        // Only events actually tagged for this channel; a relay may answer a
        // filter loosely, and the tag is what the sender committed to.
        if (!string.Equals(nostrEvent.Tag("g"), geohash, StringComparison.OrdinalIgnoreCase)) return;

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(nostrEvent.CreatedAt);
        string nickname = nostrEvent.Tag("n")?.Trim() ?? string.Empty;
        if (nickname.Length == 0) nickname = "anon";
        string displayName = FormatDisplayName(nickname, nostrEvent.Pubkey);

        bool isMine = CurrentIdentity is not null &&
                      string.Equals(nostrEvent.Pubkey, CurrentIdentity.PublicKeyHex, StringComparison.OrdinalIgnoreCase);

        TouchParticipant(nostrEvent.Pubkey, displayName, timestamp);

        if (nostrEvent.Kind == NostrKind.GeohashPresence) return;
        if (nostrEvent.Kind != NostrKind.Ephemeral) return;
        if (nostrEvent.Content.Length == 0) return;

        lock (_gate)
        {
            if (!_displayedIds.Add(nostrEvent.Id)) return;
        }

        MessageReceived?.Invoke(new ChatMessage(
            nostrEvent.Id,
            nostrEvent.Pubkey,
            displayName,
            nostrEvent.Content,
            timestamp,
            isMine));
    }

    private void TouchParticipant(string pubkey, string displayName, DateTimeOffset seenAt)
    {
        // Presence events carry no nickname, so they must not overwrite a name
        // learned from a chat message with a bare "anon#....".
        bool changed;
        lock (_gate)
        {
            _participants.TryGetValue(pubkey, out var existing);
            string name = displayName.StartsWith("anon#", StringComparison.Ordinal) && existing is not null
                ? existing.DisplayName
                : displayName;

            changed = existing is null || existing.DisplayName != name;
            _participants[pubkey] = new Participant(pubkey, name, seenAt);
        }

        // The list is time-filtered on read, so refresh either way.
        ParticipantsChanged?.Invoke();
        _ = changed;
    }

    private void StartPresenceLoop(string geohash)
    {
        if (!PresencePrecisions.Contains(geohash.Length))
        {
            Log?.Invoke($"presence   off at this precision ({geohash.Length} chars) — location privacy");
            return;
        }

        var cts = new CancellationTokenSource();
        _presenceCts = cts;
        var identity = CurrentIdentity!;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // Decorrelation delay: never announce the instant we connect.
                    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2, 6)), cts.Token).ConfigureAwait(false);

                    var presence = new NostrEvent
                    {
                        Pubkey = identity.PublicKeyHex,
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Kind = NostrKind.GeohashPresence,
                        Tags = new List<List<string>> { new() { "g", geohash } },
                        Content = string.Empty
                    }.Sign(identity.PrivateKey);

                    _pool.Publish(presence);

                    double seconds = PresenceMin.TotalSeconds +
                                     Random.Shared.NextDouble() * (PresenceMax - PresenceMin).TotalSeconds;
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Left the channel.
            }
        }, cts.Token);
    }

    /// <summary>bitchat's convention: nickname plus the last four hex of the pubkey.</summary>
    public static string FormatDisplayName(string nickname, string pubkeyHex)
    {
        string name = string.IsNullOrWhiteSpace(nickname) ? "anon" : nickname.Trim();
        string suffix = pubkeyHex.Length >= 4 ? pubkeyHex[^4..] : pubkeyHex;
        return name + "#" + suffix;
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync().ConfigureAwait(false);
        await _pool.DisposeAsync().ConfigureAwait(false);
    }
}
