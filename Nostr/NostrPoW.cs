using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;

namespace BitchatWin.Nostr;

/// <summary>
/// NIP-13 proof of work. bitchat attaches a small amount of work to geohash
/// messages so relays that rate-limit unauthenticated writes still accept them.
/// The cost is deliberately tiny — 8 bits is a couple hundred hashes.
/// </summary>
public static class NostrPoW
{
    /// Difficulty the Swift client commits to, in leading zero bits.
    public const int TargetBits = 8;

    /// Mining is best-effort: past this the message ships as-is rather than waiting.
    private static readonly TimeSpan TimeCap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns a <c>["nonce", value, target]</c> tag whose event id meets the
    /// target, or null if the cap expired first. The caller must sign the event
    /// with the same <paramref name="createdAt"/> the nonce was mined against.
    /// </summary>
    public static List<string>? MineNonceTag(
        string pubkey,
        long createdAt,
        int kind,
        IReadOnlyList<List<string>> baseTags,
        string content,
        int targetBits = TargetBits)
    {
        int target = Math.Clamp(targetBits, 0, 256);

        var probe = new NostrEvent
        {
            Pubkey = pubkey,
            CreatedAt = createdAt,
            Kind = kind,
            Content = content
        };
        foreach (var tag in baseTags) probe.Tags.Add(tag);

        var nonceTag = new List<string> { "nonce", "0000000000000000", target.ToString() };
        probe.Tags.Add(nonceTag);

        var stopwatch = Stopwatch.StartNew();
        ulong nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        long attempts = 0;

        while (true)
        {
            nonceTag[1] = nonce.ToString("x16");

            if (LeadingZeroBits(SHA256.HashData(probe.CanonicalBytes())) >= target)
            {
                return new List<string> { "nonce", nonceTag[1], target.ToString() };
            }

            nonce++;
            attempts++;
            if ((attempts & 0x3FF) == 0 && stopwatch.Elapsed >= TimeCap) return null;
        }
    }

    /// <summary>Difficulty actually achieved by an event id, in leading zero bits.</summary>
    public static int DifficultyOf(string eventIdHex)
    {
        try
        {
            return LeadingZeroBits(Convert.FromHexString(eventIdHex));
        }
        catch
        {
            return 0;
        }
    }

    private static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        int bits = 0;
        foreach (byte b in hash)
        {
            if (b == 0) { bits += 8; continue; }
            bits += System.Numerics.BitOperations.LeadingZeroCount(b) - 24;
            break;
        }
        return bits;
    }
}
