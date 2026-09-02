using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BitchatWin.Nostr;

/// <summary>Event kinds bitchat uses. Values must match the Swift client.</summary>
public static class NostrKind
{
    public const int TextNote = 1;
    /// Geohash channel chat message.
    public const int Ephemeral = 20000;
    /// Geohash presence heartbeat (empty content).
    public const int GeohashPresence = 20001;
    /// Outer envelope of a bitchat private message.
    public const int GiftWrap = 1059;
}

/// <summary>
/// A Nostr event, with the NIP-01 canonical serialisation used for the event id
/// and BIP-340 Schnorr signing.
/// </summary>
public sealed class NostrEvent
{
    public string Id { get; set; } = string.Empty;
    public string Pubkey { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public int Kind { get; set; }
    public List<List<string>> Tags { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public string? Sig { get; set; }

    /// <summary>First value of the first tag with this name, or null.</summary>
    public string? Tag(string name) =>
        Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == name)?[1];

    /// <summary>
    /// NIP-01 canonical form: <c>[0,pubkey,created_at,kind,tags,content]</c>,
    /// compact, with only the escapes the spec requires. The event id is the
    /// SHA-256 of these UTF-8 bytes, so this must be byte-exact.
    /// </summary>
    public byte[] CanonicalBytes()
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,");
        AppendJsonString(sb, Pubkey);
        sb.Append(',').Append(CreatedAt).Append(',').Append(Kind).Append(',');

        sb.Append('[');
        for (int i = 0; i < Tags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            for (int j = 0; j < Tags[i].Count; j++)
            {
                if (j > 0) sb.Append(',');
                AppendJsonString(sb, Tags[i][j]);
            }
            sb.Append(']');
        }
        sb.Append("],");

        AppendJsonString(sb, Content);
        sb.Append(']');

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public string ComputeId() => Convert.ToHexString(SHA256.HashData(CanonicalBytes())).ToLowerInvariant();

    /// <summary>Fills in <see cref="Id"/> and <see cref="Sig"/> in place.</summary>
    public NostrEvent Sign(byte[] privateKey32)
    {
        byte[] hash = SHA256.HashData(CanonicalBytes());
        Id = Convert.ToHexString(hash).ToLowerInvariant();
        Sig = Convert.ToHexString(NostrCrypto.SignSchnorr(privateKey32, hash)).ToLowerInvariant();
        return this;
    }

    /// <summary>
    /// True when the id matches the content and the Schnorr signature verifies
    /// against the author's key. Relay output is untrusted; nothing reaches the
    /// UI without passing this.
    /// </summary>
    public bool VerifySignature()
    {
        if (string.IsNullOrEmpty(Sig) || Sig.Length != 128) return false;
        if (Pubkey.Length != 64) return false;

        byte[] hash;
        try
        {
            hash = SHA256.HashData(CanonicalBytes());
            if (!Convert.ToHexString(hash).Equals(Id, StringComparison.OrdinalIgnoreCase)) return false;

            return NostrCrypto.VerifySchnorr(
                Convert.FromHexString(Pubkey),
                hash,
                Convert.FromHexString(Sig));
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Wire JSON object sent inside <c>["EVENT", ...]</c>.</summary>
    public string ToWireJson()
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("id", Id);
        writer.WriteString("pubkey", Pubkey);
        writer.WriteNumber("created_at", CreatedAt);
        writer.WriteNumber("kind", Kind);
        writer.WriteStartArray("tags");
        foreach (var tag in Tags)
        {
            writer.WriteStartArray();
            foreach (string value in tag) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteString("content", Content);
        writer.WriteString("sig", Sig ?? string.Empty);
        writer.WriteEndObject();
    }

    /// <summary>Parses a relay-supplied event object. Returns null if malformed.</summary>
    public static NostrEvent? FromJson(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) return null;

            var ev = new NostrEvent
            {
                Id = element.GetProperty("id").GetString() ?? string.Empty,
                Pubkey = element.GetProperty("pubkey").GetString() ?? string.Empty,
                CreatedAt = element.GetProperty("created_at").GetInt64(),
                Kind = element.GetProperty("kind").GetInt32(),
                Content = element.GetProperty("content").GetString() ?? string.Empty,
                Sig = element.TryGetProperty("sig", out var sig) ? sig.GetString() : null
            };

            if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                // Bound untrusted tag arrays the way the Swift client does, so a
                // hostile relay cannot drive large allocations on this path.
                if (tags.GetArrayLength() > 128) return null;
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.ValueKind != JsonValueKind.Array) continue;
                    var values = new List<string>();
                    foreach (var value in tag.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String) continue;
                        string s = value.GetString() ?? string.Empty;
                        if (s.Length > 1024) return null;
                        values.Add(s);
                    }
                    ev.Tags.Add(values);
                }
            }

            return ev;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
