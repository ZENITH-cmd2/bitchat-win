using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BitchatWin.Nostr;

/// <summary>
/// A secp256k1 identity used to sign Nostr events.
///
/// bitchat never reuses one key across location channels: each geohash gets its
/// own deterministic identity derived from a device seed, so the same device is
/// not linkable between channels. The derivation below matches
/// <c>NostrIdentityBridge.deriveIdentity(forGeohash:)</c> in the Swift client.
/// </summary>
public sealed class NostrIdentity
{
    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }
    public string PublicKeyHex { get; }
    public string Npub { get; }

    private NostrIdentity(byte[] privateKey)
    {
        PrivateKey = privateKey;
        PublicKey = NostrCrypto.GetXOnlyPublicKey(privateKey);
        PublicKeyHex = Convert.ToHexString(PublicKey).ToLowerInvariant();
        Npub = Bech32.Encode("npub", PublicKey);
    }

    public static NostrIdentity FromPrivateKey(byte[] privateKey32)
    {
        if (privateKey32.Length != 32) throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey32));
        return new NostrIdentity(privateKey32);
    }

    public static NostrIdentity Generate()
    {
        while (true)
        {
            byte[] candidate = RandomNumberGenerator.GetBytes(32);
            if (NostrCrypto.IsValidPrivateKey(candidate)) return new NostrIdentity(candidate);
        }
    }

    /// <summary>
    /// Deterministic, per-geohash identity: HMAC-SHA256(seed, geohash ‖ uint32BE(i)),
    /// retrying on the vanishingly rare candidate that is not a valid scalar.
    /// </summary>
    public static NostrIdentity DeriveForGeohash(byte[] deviceSeed, string geohash)
    {
        byte[] message = Encoding.UTF8.GetBytes(geohash);

        for (uint i = 0; i < 10; i++)
        {
            byte[] input = new byte[message.Length + 4];
            message.CopyTo(input, 0);
            BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(message.Length), i);

            byte[] candidate = HMACSHA256.HashData(deviceSeed, input);
            if (NostrCrypto.IsValidPrivateKey(candidate)) return new NostrIdentity(candidate);
        }

        // Same last-resort path as the Swift client, so both agree even here.
        byte[] combined = new byte[deviceSeed.Length + message.Length];
        deviceSeed.CopyTo(combined, 0);
        message.CopyTo(combined, deviceSeed.Length);
        return new NostrIdentity(SHA256.HashData(combined));
    }

    /// <summary>Identity for a mesh-bridge rendezvous cell (distinct label keeps it unlinkable).</summary>
    public static NostrIdentity DeriveForBridgeRendezvous(byte[] deviceSeed, string cell) =>
        DeriveForGeohash(deviceSeed, "bridge|" + cell);
}
