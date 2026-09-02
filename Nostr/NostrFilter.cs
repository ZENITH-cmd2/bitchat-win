using System;
using System.Collections.Generic;
using System.Text.Json;

namespace BitchatWin.Nostr;

/// <summary>A Nostr REQ filter. Tag filters are written with the <c>#</c> prefix.</summary>
public sealed class NostrFilter
{
    public List<string>? Ids { get; set; }
    public List<string>? Authors { get; set; }
    public List<int>? Kinds { get; set; }
    public long? Since { get; set; }
    public long? Until { get; set; }
    public int? Limit { get; set; }
    public Dictionary<string, List<string>> TagFilters { get; } = new();

    /// <summary>
    /// Location channel traffic: chat (20000) and presence (20001) tagged with
    /// the geohash. Matches <c>NostrFilter.geohashEphemeral</c> in the Swift client.
    /// </summary>
    public static NostrFilter GeohashEphemeral(string geohash, DateTimeOffset? since = null, int limit = 200)
    {
        var filter = new NostrFilter
        {
            Kinds = new List<int> { NostrKind.Ephemeral, NostrKind.GeohashPresence },
            Since = since?.ToUnixTimeSeconds(),
            Limit = limit
        };
        filter.TagFilters["g"] = new List<string> { geohash };
        return filter;
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        if (Ids is not null) WriteStrings(writer, "ids", Ids);
        if (Authors is not null) WriteStrings(writer, "authors", Authors);
        if (Kinds is not null)
        {
            writer.WriteStartArray("kinds");
            foreach (int kind in Kinds) writer.WriteNumberValue(kind);
            writer.WriteEndArray();
        }
        if (Since is not null) writer.WriteNumber("since", Since.Value);
        if (Until is not null) writer.WriteNumber("until", Until.Value);
        if (Limit is not null) writer.WriteNumber("limit", Limit.Value);

        foreach (var (tag, values) in TagFilters) WriteStrings(writer, "#" + tag, values);

        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, List<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
