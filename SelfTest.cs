using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BitchatWin.Nostr;
using BitchatWin.Protocol;
using BitchatWin.Services;

namespace BitchatWin;

/// <summary>
/// Headless checks for the parts that have to be byte-exact to interoperate:
/// canonical event serialisation, event ids, Schnorr signatures, geohash
/// encoding and relay selection.
///
/// Run with <c>--selftest</c>; <c>--listen &lt;geohash&gt;</c> additionally joins a
/// live channel read-only and reports whether real bitchat events verify.
/// Results go to stdout and to selftest-output.txt.
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _failures;

    public static async Task<int> RunAsync(string[] args)
    {
        Line("=== bitchat-win self test ===");
        Line(string.Empty);

        CheckGeohash();
        CheckCanonicalSerialisation();
        CheckSignatures();
        CheckIdentityDerivation();
        CheckRelaySelection();

        int listenIndex = Array.IndexOf(args, "--listen");
        if (listenIndex >= 0 && listenIndex + 1 < args.Length)
        {
            await ListenAsync(args[listenIndex + 1]).ConfigureAwait(false);
        }

        if (args.Contains("--scan")) await ScanAsync().ConfigureAwait(false);

        if (args.Contains("--sendtest")) await SendTestAsync().ConfigureAwait(false);

        if (args.Contains("--localtest")) await LocalRelayTestAsync().ConfigureAwait(false);

        Line(string.Empty);
        Line(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");

        try
        {
            File.WriteAllText("selftest-output.txt", Report.ToString());
        }
        catch
        {
            // Report already went to stdout.
        }

        return _failures == 0 ? 0 : 1;
    }

    private static void CheckGeohash()
    {
        Line("[geohash]");

        // Reference vectors from the standard base32 geohash definition.
        Check("encode(57.64911,10.40744,11)", Geohash.Encode(57.64911, 10.40744, 11), "u4pruydqqvj");
        Check("encode(45.4642,9.1900,5) milan", Geohash.Encode(45.4642, 9.1900, 5), "u0nd9");
        Check("encode(40.7128,-74.0060,7) nyc", Geohash.Encode(40.7128, -74.0060, 7), "dr5regw");

        var (lat, lon) = Geohash.DecodeCenter("u0nd9");
        bool roundTrips = Math.Abs(lat - 45.4642) < 0.03 && Math.Abs(lon - 9.19) < 0.03;
        Check("decodeCenter(u0nd9) round-trips", roundTrips ? "ok" : $"({lat},{lon})", "ok");

        Check("isValid(u0nd9)", Geohash.IsValid("u0nd9").ToString(), "True");
        Check("isValid(ail) rejects a,i,l", Geohash.IsValid("ail").ToString(), "False");
        Line(string.Empty);
    }

    private static void CheckCanonicalSerialisation()
    {
        Line("[canonical serialisation]");

        var nostrEvent = new NostrEvent
        {
            Pubkey = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
            CreatedAt = 1700000000,
            Kind = NostrKind.Ephemeral,
            Tags = new List<List<string>>
            {
                new() { "g", "u0nd9" },
                new() { "n", "anon" }
            },
            Content = "ciao \"mondo\"\nsecond line\ttabbed / slash è àccentata"
        };

        string canonical = Encoding.UTF8.GetString(nostrEvent.CanonicalBytes());
        Line("  canonical: " + canonical);

        // NIP-01 requires exactly these escapes and nothing else: no escaped
        // forward slash, and non-ASCII stays literal UTF-8.
        Check("no escaped slash", canonical.Contains("\\/") ? "escaped" : "literal", "literal");
        Check("non-ASCII literal", canonical.Contains("è") ? "literal" : "escaped", "literal");
        Check("quote escaped", canonical.Contains("\\\"mondo\\\"") ? "yes" : "no", "yes");
        Check("newline escaped", canonical.Contains("\\n") ? "yes" : "no", "yes");
        Check("tab escaped", canonical.Contains("\\t") ? "yes" : "no", "yes");
        Check("compact (no spaces after commas)", canonical.Contains(", ") ? "spaced" : "compact", "compact");
        Line("  id: " + nostrEvent.ComputeId());
        Line(string.Empty);
    }

    private static void CheckSignatures()
    {
        Line("[schnorr]");

        var identity = NostrIdentity.Generate();
        Check("pubkey is 32 bytes", identity.PublicKey.Length.ToString(), "32");
        Check("npub prefix", identity.Npub[..4], "npub");

        var nostrEvent = new NostrEvent
        {
            Pubkey = identity.PublicKeyHex,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = NostrKind.Ephemeral,
            Tags = new List<List<string>> { new() { "g", "u0nd9" }, new() { "n", "tester" } },
            Content = "hello from windows"
        }.Sign(identity.PrivateKey);

        Check("id matches content", nostrEvent.Id, nostrEvent.ComputeId());
        Check("signature length", (nostrEvent.Sig?.Length ?? 0).ToString(), "128");
        Check("signature verifies", nostrEvent.VerifySignature().ToString(), "True");

        // Any mutation must invalidate the event, or a relay could be fed a
        // message the author never wrote.
        string original = nostrEvent.Content;
        nostrEvent.Content = original + "!";
        Check("tampered content rejected", nostrEvent.VerifySignature().ToString(), "False");
        nostrEvent.Content = original;
        Check("restored content accepted", nostrEvent.VerifySignature().ToString(), "True");

        // Proof of work: the mined id must actually meet the target.
        var tags = new List<List<string>> { new() { "g", "u0nd9" } };
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonceTag = NostrPoW.MineNonceTag(identity.PublicKeyHex, createdAt, NostrKind.Ephemeral, tags, "pow test");
        if (nonceTag is null)
        {
            Check("pow mined", "timeout", "ok");
        }
        else
        {
            tags.Add(nonceTag);
            var mined = new NostrEvent
            {
                Pubkey = identity.PublicKeyHex,
                CreatedAt = createdAt,
                Kind = NostrKind.Ephemeral,
                Tags = tags,
                Content = "pow test"
            }.Sign(identity.PrivateKey);

            Check("pow mined", "ok", "ok");
            Check("pow difficulty >= 8", (NostrPoW.DifficultyOf(mined.Id) >= 8).ToString(), "True");
            Check("pow nonce tag shape", $"{nonceTag[0]}/{nonceTag[1].Length}/{nonceTag[2]}", "nonce/16/8");
        }
        Line(string.Empty);
    }

    private static void CheckIdentityDerivation()
    {
        Line("[identity derivation]");

        byte[] seed = new byte[32];
        for (int i = 0; i < seed.Length; i++) seed[i] = (byte)i;

        var a1 = NostrIdentity.DeriveForGeohash(seed, "u0nd9");
        var a2 = NostrIdentity.DeriveForGeohash(seed, "u0nd9");
        var b = NostrIdentity.DeriveForGeohash(seed, "dr5reg");

        Check("deterministic per geohash", a1.PublicKeyHex, a2.PublicKeyHex);
        Check("unlinkable across geohashes", (a1.PublicKeyHex != b.PublicKeyHex).ToString(), "True");
        Check("bridge label differs", (NostrIdentity.DeriveForBridgeRendezvous(seed, "u0nd9").PublicKeyHex != a1.PublicKeyHex).ToString(), "True");

        // The derivation is HMAC-SHA256(seed, geohash || uint32BE(0)); recompute
        // it here independently so a refactor cannot silently change identities.
        byte[] message = Encoding.UTF8.GetBytes("u0nd9");
        byte[] input = new byte[message.Length + 4];
        message.CopyTo(input, 0);
        byte[] expectedKey = System.Security.Cryptography.HMACSHA256.HashData(seed, input);
        Check("matches HMAC(seed, gh||0)", a1.PublicKeyHex, NostrIdentity.FromPrivateKey(expectedKey).PublicKeyHex);
        Line(string.Empty);
    }

    private static void CheckRelaySelection()
    {
        Line("[relay directory]");

        // Host canonicalisation decides which relays exist at all, so it has to
        // match the Swift client exactly.
        Check("strips default port", GeoRelayDirectory.NormalizeHost("no.str.cr:443") ?? "null", "no.str.cr");
        Check("keeps non-default port", GeoRelayDirectory.NormalizeHost("chorus.mikedilger.com:444") ?? "null", "chorus.mikedilger.com:444");
        Check("strips scheme and case", GeoRelayDirectory.NormalizeHost("wss://Relay.Example.COM/") ?? "null", "relay.example.com");
        Check("rejects localhost", GeoRelayDirectory.NormalizeHost("localhost") ?? "null", "null");
        Check("rejects single label", GeoRelayDirectory.NormalizeHost("example") ?? "null", "null");
        Check("rejects path", GeoRelayDirectory.NormalizeHost("relay.example.com/path") ?? "null", "null");
        Check("rejects http scheme", GeoRelayDirectory.NormalizeHost("http://relay.example.com") ?? "null", "null");

        int count = GeoRelayDirectory.Shared.Count;
        Check("directory loaded", (count > 100).ToString(), "True");
        Line($"  {count} distinct relays with coordinates");

        var milan = GeoRelayDirectory.Shared.ClosestRelays("u0nd9", 5);
        var newYork = GeoRelayDirectory.Shared.ClosestRelays("dr5reg", 5);

        Check("5 relays for milan", milan.Count.ToString(), "5");
        Check("no duplicate relays", milan.Distinct().Count().ToString(), "5");
        Check("deterministic", string.Join(",", milan), string.Join(",", GeoRelayDirectory.Shared.ClosestRelays("u0nd9", 5)));
        Check("geography matters", (string.Join(",", milan) != string.Join(",", newYork)).ToString(), "True");
        Line("  milan:    " + string.Join(" ", milan));
        Line("  new york: " + string.Join(" ", newYork));
        Line(string.Empty);
    }

    /// <summary>
    /// Joins a live channel read-only for 30 seconds. Every event that arrives
    /// was signed by a real client, so a verified event proves this client's
    /// canonical serialisation matches the network's byte for byte.
    /// </summary>
    private static async Task ListenAsync(string geohash)
    {
        Line($"[live listen #{geohash}]");

        await using var pool = new RelayPool();
        int received = 0, chat = 0, presence = 0;

        pool.RelayStatusChanged += (url, connected, detail) =>
            Line($"  {(connected ? "up  " : "down")} {url}{(detail is null ? "" : "  " + detail)}");

        pool.EventReceived += ev =>
        {
            // RelayPool only surfaces events whose id and signature verify.
            Interlocked.Increment(ref received);
            if (ev.Kind == NostrKind.GeohashPresence) Interlocked.Increment(ref presence);
            else
            {
                Interlocked.Increment(ref chat);
                Line($"  chat  {GeohashChannelService.FormatDisplayName(ev.Tag("n") ?? "anon", ev.Pubkey)}: {ev.Content}");
            }
        };

        var relays = GeoRelayDirectory.Shared.ClosestRelays(geohash, 5);
        pool.SetRelays(relays);
        pool.Subscribe("selftest", NostrFilter.GeohashEphemeral(geohash, DateTimeOffset.UtcNow.AddHours(-1), 200));

        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Line($"  verified events: {received}  (chat {chat}, presence {presence})");
        Line(received > 0
            ? "  every event above passed id + schnorr verification against a real signer"
            : "  no traffic in this window (ephemeral events are not stored by relays)");
        Line(string.Empty);
    }

    /// <summary>
    /// Read-only survey of live bitchat traffic: subscribes to the location
    /// channel kinds with no geohash filter and tallies what arrives, so an
    /// active channel can be found without guessing. Nothing is published.
    /// </summary>
    private static async Task ScanAsync()
    {
        Line("[scan: live bitchat traffic, 45s, read-only]");

        await using var pool = new RelayPool();
        var byGeohash = new Dictionary<string, (int Chat, int Presence)>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<string>();
        var gate = new object();
        int verified = 0;

        pool.EventReceived += ev =>
        {
            string? geohash = ev.Tag("g");
            if (geohash is null) return;

            lock (gate)
            {
                verified++;
                byGeohash.TryGetValue(geohash, out var counts);
                if (ev.Kind == NostrKind.GeohashPresence) counts.Presence++;
                else
                {
                    counts.Chat++;
                    if (samples.Count < 12 && ev.Content.Length > 0)
                    {
                        samples.Add($"#{geohash}  {GeohashChannelService.FormatDisplayName(ev.Tag("n") ?? "anon", ev.Pubkey)}: {Truncate(ev.Content, 80)}");
                    }
                }
                byGeohash[geohash] = counts;
            }
        };

        // The four relays every bitchat client uses, plus geographic spread.
        var relays = new List<string>
        {
            "wss://relay.damus.io", "wss://nos.lol", "wss://relay.primal.net", "wss://offchain.pub",
            "wss://bitchat.nostr1.com"
        };
        foreach (string geohash in new[] { "u0", "dr", "9q", "gc", "w2", "sp" })
        {
            foreach (string relay in GeoRelayDirectory.Shared.ClosestRelays(geohash, 3))
            {
                if (!relays.Contains(relay)) relays.Add(relay);
            }
        }

        Line($"  {relays.Count} relays");
        pool.SetRelays(relays);

        var filter = new NostrFilter
        {
            Kinds = new List<int> { NostrKind.Ephemeral, NostrKind.GeohashPresence },
            Limit = 500
        };
        pool.Subscribe("scan", filter);

        await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        lock (gate)
        {
            Line($"  connected relays: {pool.ConnectedCount}/{pool.Relays.Count}");
            Line($"  verified events:  {verified} across {byGeohash.Count} channels");
            Line(string.Empty);

            foreach (var entry in byGeohash.OrderByDescending(e => e.Value.Chat + e.Value.Presence).Take(15))
            {
                Line($"    #{entry.Key,-10} chat {entry.Value.Chat,4}   presence {entry.Value.Presence,4}");
            }

            if (samples.Count > 0)
            {
                Line(string.Empty);
                Line("  live messages (all signature-verified):");
                foreach (string sample in samples) Line("    " + sample);
            }
        }
        Line(string.Empty);
    }

    /// <summary>
    /// Publishes exactly one event and reports what each relay says about it.
    /// The target is a geohash over Point Nemo — the most remote point in any
    /// ocean — so the test cannot land in anyone's conversation, while still
    /// exercising the real signing, proof-of-work and publish path.
    /// </summary>
    private static async Task SendTestAsync()
    {
        string geohash = Geohash.Encode(-48.8767, -123.3933, 8);
        Line($"[send test → #{geohash} (Point Nemo, canale deserto)]");

        await using var pool = new RelayPool();
        var acks = new List<string>();
        var gate = new object();
        var echoed = new List<string>();

        pool.RelayStatusChanged += (url, connected, detail) =>
            Line($"  {(connected ? "up  " : "down")} {url}{(detail is null ? "" : "  " + detail)}");

        pool.PublishAck += (url, id, accepted, reason) =>
        {
            lock (gate) acks.Add($"    {(accepted ? "ACCEPTED" : "REJECTED")}  {url}{(reason.Length == 0 ? "" : "  → " + reason)}");
        };
        pool.Notice += (url, message) => Line($"  notice {url}: {message}");

        // Subscribing first means the round trip is verified too: a relay that
        // accepts the event should hand it straight back on this subscription.
        pool.EventReceived += ev => { lock (gate) echoed.Add(ev.Id); };

        var relays = GeoRelayDirectory.Shared.ClosestRelays(geohash, 5);
        pool.SetRelays(relays);
        pool.Subscribe("sendtest", NostrFilter.GeohashEphemeral(geohash, DateTimeOffset.UtcNow.AddMinutes(-5), 20));

        await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        Line($"  connected: {pool.ConnectedCount}/{pool.Relays.Count}");

        var identity = NostrIdentity.Generate();
        var tags = new List<List<string>> { new() { "g", geohash }, new() { "n", "bitchat-win" } };
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var nonceTag = NostrPoW.MineNonceTag(identity.PublicKeyHex, createdAt, NostrKind.Ephemeral, tags, "connectivity test");
        if (nonceTag is not null) tags.Add(nonceTag);

        var probe = new NostrEvent
        {
            Pubkey = identity.PublicKeyHex,
            CreatedAt = createdAt,
            Kind = NostrKind.Ephemeral,
            Tags = tags,
            Content = "connectivity test"
        }.Sign(identity.PrivateKey);

        Line($"  event id:  {probe.Id}");
        Line($"  pow:       {NostrPoW.DifficultyOf(probe.Id)} bit");
        Line($"  self-check: {(probe.VerifySignature() ? "signature valid" : "SIGNATURE INVALID")}");

        pool.Publish(probe);
        await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);

        lock (gate)
        {
            Line(string.Empty);
            if (acks.Count == 0) Line("    nessuna risposta OK (alcuni relay non la inviano per gli eventi effimeri)");
            foreach (string ack in acks) Line(ack);

            bool accepted = acks.Any(a => a.Contains("ACCEPTED"));
            bool roundTrip = echoed.Contains(probe.Id);
            Line(string.Empty);
            Line($"  accettato da almeno un relay: {(accepted ? "SI" : "no")}");
            Line($"  tornato indietro sulla sottoscrizione: {(roundTrip ? "SI" : "no")}");

            if (!accepted && !roundTrip) _failures++;
        }
        Line(string.Empty);
    }

    /// <summary>
    /// Two clients talking through the built-in relay over loopback, with no
    /// internet involved at any point. This is the offline path end to end:
    /// WebSocket handshake, NIP-01 framing, signature verification at the relay,
    /// history replay and live fan-out.
    /// </summary>
    private static async Task LocalRelayTestAsync()
    {
        Line("[relay locale: due client, nessun internet]");

        await using var server = new LocalRelayServer();
        server.Log += line => Line("  server: " + line);
        int port = server.Start(0); // 0 = let the OS pick a free port
        string url = $"ws://127.0.0.1:{port}";
        Line($"  relay su {url}");

        const string geohash = "u0nd9";
        byte[] seedA = new byte[32];
        byte[] seedB = new byte[32];
        for (int i = 0; i < 32; i++) { seedA[i] = (byte)i; seedB[i] = (byte)(255 - i); }

        var alice = NostrIdentity.DeriveForGeohash(seedA, geohash);
        var bob = NostrIdentity.DeriveForGeohash(seedB, geohash);

        await using var listener = new RelayPool();
        var received = new List<NostrEvent>();
        var gate = new object();
        listener.EventReceived += ev => { lock (gate) received.Add(ev); };
        listener.SetRelays(new[] { url });
        listener.Subscribe("local", NostrFilter.GeohashEphemeral(geohash, DateTimeOffset.UtcNow.AddMinutes(-5), 50));

        await using var sender = new RelayPool();
        sender.SetRelays(new[] { url });

        await Task.Delay(2000).ConfigureAwait(false);
        Line($"  client connessi al relay: {server.ClientCount}");

        var message = new NostrEvent
        {
            Pubkey = bob.PublicKeyHex,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = NostrKind.Ephemeral,
            Tags = new List<List<string>> { new() { "g", geohash }, new() { "n", "bob" } },
            Content = "ciao senza internet"
        }.Sign(bob.PrivateKey);

        sender.Publish(message);
        await Task.Delay(2500).ConfigureAwait(false);

        bool delivered;
        lock (gate) delivered = received.Any(e => e.Id == message.Id && e.Content == "ciao senza internet");
        Check("messaggio consegnato all'altro client", delivered.ToString(), "True");
        Check("relay ha memorizzato l'evento", (server.StoredEventCount >= 1).ToString(), "True");

        // A relay that stores unverified events would let anyone forge messages,
        // so prove the check is really enforced: same content, broken signature.
        var forged = new NostrEvent
        {
            Pubkey = alice.PublicKeyHex,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = NostrKind.Ephemeral,
            Tags = new List<List<string>> { new() { "g", geohash }, new() { "n", "impostore" } },
            Content = "messaggio falsificato"
        }.Sign(bob.PrivateKey); // signed by Bob, but claiming to be Alice
        forged.Pubkey = alice.PublicKeyHex;

        int before = server.StoredEventCount;
        sender.Publish(forged);
        await Task.Delay(1500).ConfigureAwait(false);
        Check("evento con firma non valida rifiutato", (server.StoredEventCount == before).ToString(), "True");

        // History replay: a client joining afterwards must still see the message.
        await using var latecomer = new RelayPool();
        var late = new List<NostrEvent>();
        latecomer.EventReceived += ev => { lock (gate) late.Add(ev); };
        latecomer.SetRelays(new[] { url });
        latecomer.Subscribe("late", NostrFilter.GeohashEphemeral(geohash, DateTimeOffset.UtcNow.AddMinutes(-5), 50));
        await Task.Delay(2500).ConfigureAwait(false);

        bool replayed;
        lock (gate) replayed = late.Any(e => e.Id == message.Id);
        Check("chi arriva dopo riceve lo storico", replayed.ToString(), "True");

        await server.StopAsync().ConfigureAwait(false);
        Line(string.Empty);
    }

    private static string Truncate(string text, int max)
    {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static void Check(string name, string actual, string expected)
    {
        bool ok = actual == expected;
        if (!ok) _failures++;
        Line($"  {(ok ? "PASS" : "FAIL")}  {name}" + (ok ? string.Empty : $"  → got '{actual}', want '{expected}'"));
    }

    private static void Line(string text)
    {
        Report.AppendLine(text);
        Console.WriteLine(text);
    }
}
