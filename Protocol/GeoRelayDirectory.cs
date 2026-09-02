using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BitchatWin.Protocol;

/// <summary>
/// Directory of Nostr relays with approximate GPS coordinates, used to pick the
/// relays a geohash channel lives on.
///
/// Publishers and subscribers must agree on the relay set, so the selection
/// rule mirrors bitchat's <c>GeoRelayDirectory.swift</c> exactly: sort every
/// known relay by great-circle distance from the geohash centre, break ties on
/// the host name, take the first N.
/// </summary>
public sealed class GeoRelayDirectory
{
    public sealed record Entry(string Host, double Lat, double Lon);

    /// The same CSV the iOS and Android clients read, refreshed by pull request.
    private const string RemoteUrl =
        "https://raw.githubusercontent.com/permissionlesstech/bitchat/refs/heads/main/relays/online_relays_gps.csv";

    public static GeoRelayDirectory Shared { get; } = new();

    private readonly object _gate = new();
    private List<Entry> _entries = new();

    public int Count { get { lock (_gate) return _entries.Count; } }

    private GeoRelayDirectory()
    {
        _entries = LoadEmbedded();
    }

    private static List<Entry> LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("online_relays_gps.csv", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new List<Entry>();

        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return new List<Entry>();

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()) ?? new List<Entry>();
    }

    /// <summary>
    /// Parses the CSV, or returns null if the file is not trustworthy.
    ///
    /// This deliberately mirrors <c>GeoRelayDirectory.validatedEntries</c> in the
    /// Swift client, including the all-or-nothing rejection: relay selection only
    /// brings people together if every client derives the same set from the same
    /// file, so "skip the bad row and carry on" would silently desynchronise this
    /// client from the network.
    /// </summary>
    public static List<Entry>? Parse(string csv)
    {
        if (csv.Length == 0 || csv.StartsWith('﻿')) return null;

        var lines = csv.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return null;

        var header = lines[0].Split(',').Select(p => p.Trim().ToLowerInvariant()).ToList();
        bool headerOk =
            header.SequenceEqual(new[] { "relay url", "latitude", "longitude" }) ||
            header.SequenceEqual(new[] { "relay url", "lat", "lon" });
        if (!headerOk) return null;

        var entriesByHost = new Dictionary<string, Entry>(StringComparer.Ordinal);

        foreach (string line in lines.Skip(1))
        {
            string[] parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length != 3) return null;

            string? host = NormalizeHost(parts[0]);
            if (host is null) return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) return null;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) return null;
            if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
            if (lat is < -90 or > 90 || lon is < -180 or > 180) return null;

            var entry = new Entry(host, lat, lon);
            // One endpoint cannot truthfully sit at two coordinates; row order
            // must not decide which location clients trust.
            if (entriesByHost.TryGetValue(host, out var existing) && existing != entry) return null;
            entriesByHost[host] = entry;
        }

        return entriesByHost.Values
            .OrderBy(e => e.Host, StringComparer.Ordinal)
            .ThenBy(e => e.Lat)
            .ThenBy(e => e.Lon)
            .ToList();
    }

    /// <summary>
    /// Canonical host form, matching the Swift client's
    /// <c>validatedDirectoryAddress</c>. The important part is the last step:
    /// an explicit <c>:443</c> is dropped, which collapses rows like
    /// <c>no.str.cr</c> and <c>no.str.cr:443</c> into one relay. Keeping them
    /// apart would leave this client with fewer distinct relays than everyone
    /// else picks for the same geohash.
    /// </summary>
    public static string? NormalizeHost(string rawValue)
    {
        string value = rawValue.Trim();
        if (value.Length == 0) return null;
        foreach (char c in value)
        {
            if (c > 127 || char.IsControl(c)) return null;
        }

        string candidate = value.Contains("://") ? value : "wss://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;

        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "wss" && scheme != "https") return null;
        if (uri.UserInfo.Length > 0) return null;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        if (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/") return null;

        string host = uri.Host.ToLowerInvariant();
        if (host.Length == 0 || host.Length > 253) return null;
        if (host.EndsWith('.')) return null;
        if (host == "localhost" || host.EndsWith(".localhost") ||
            host.EndsWith(".local") || host.EndsWith(".internal")) return null;

        string[] labels = host.Split('.');
        if (labels.Length < 2) return null;
        if (labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit))) return null;
        foreach (string label in labels)
        {
            if (label.Length is < 1 or > 63) return null;
            if (label[0] == '-' || label[^1] == '-') return null;
            if (!label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) return null;
        }

        // Uri reports -1 when no port was written; wss has no default it knows.
        int port = uri.Port;
        if (port == -1) return host;
        if (port is < 1 or > 65535) return null;
        return port == 443 ? host : $"{host}:{port}";
    }

    /// <summary>Up to <paramref name="count"/> relay URLs closest to the geohash centre.</summary>
    public IReadOnlyList<string> ClosestRelays(string geohash, int count = 5)
    {
        var (lat, lon) = Geohash.DecodeCenter(geohash);
        return ClosestRelays(lat, lon, count);
    }

    public IReadOnlyList<string> ClosestRelays(double lat, double lon, int count = 5)
    {
        List<Entry> snapshot;
        lock (_gate) snapshot = _entries;

        if (snapshot.Count == 0 || count <= 0) return Array.Empty<string>();

        return snapshot
            .Select(e => (Entry: e, Distance: HaversineKm(lat, lon, e.Lat, e.Lon)))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Entry.Host, StringComparer.Ordinal)
            .Take(count)
            .Select(x => $"wss://{x.Entry.Host}")
            .ToList();
    }

    /// <summary>
    /// Best-effort refresh from the upstream CSV. Failure is silent and leaves
    /// the embedded copy in place — a stale directory still routes.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string csv = await http.GetStringAsync(RemoteUrl, cancellationToken).ConfigureAwait(false);
            var parsed = Parse(csv);
            // A rejected, truncated or empty response must not shrink the directory.
            if (parsed is null || parsed.Count < 50) return false;

            lock (_gate) _entries = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
