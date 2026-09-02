using System;
using NBitcoin.Secp256k1;

namespace BitchatWin.Nostr;

/// <summary>
/// secp256k1 operations behind a small surface: BIP-340 Schnorr signing and
/// verification, plus x-only public keys, which is all Nostr needs.
/// </summary>
public static class NostrCrypto
{
    /// <summary>True when the 32 bytes are a usable secp256k1 secret key.</summary>
    public static bool IsValidPrivateKey(ReadOnlySpan<byte> privateKey32)
    {
        if (privateKey32.Length != 32) return false;
        return Context.Instance.TryCreateECPrivKey(privateKey32, out var key) && key is not null;
    }

    /// <summary>x-only (32-byte) public key for a secret key.</summary>
    public static byte[] GetXOnlyPublicKey(ReadOnlySpan<byte> privateKey32)
    {
        if (!Context.Instance.TryCreateECPrivKey(privateKey32, out var key) || key is null)
            throw new ArgumentException("Invalid secp256k1 private key", nameof(privateKey32));

        var xonly = key.CreatePubKey().ToXOnlyPubKey();
        byte[] output = new byte[32];
        xonly.WriteToSpan(output);
        return output;
    }

    /// <summary>BIP-340 Schnorr signature over a 32-byte message hash.</summary>
    public static byte[] SignSchnorr(ReadOnlySpan<byte> privateKey32, ReadOnlySpan<byte> messageHash32)
    {
        if (!Context.Instance.TryCreateECPrivKey(privateKey32, out var key) || key is null)
            throw new ArgumentException("Invalid secp256k1 private key", nameof(privateKey32));

        if (!key.TrySignBIP340(messageHash32, null, out var signature) || signature is null)
            throw new InvalidOperationException("Schnorr signing failed");

        byte[] output = new byte[64];
        signature.WriteToSpan(output);
        return output;
    }

    /// <summary>Verifies a BIP-340 signature against an x-only public key.</summary>
    public static bool VerifySchnorr(ReadOnlySpan<byte> xOnlyPublicKey32, ReadOnlySpan<byte> messageHash32, ReadOnlySpan<byte> signature64)
    {
        if (xOnlyPublicKey32.Length != 32 || messageHash32.Length != 32 || signature64.Length != 64) return false;

        try
        {
            if (!Context.Instance.TryCreateXOnlyPubKey(xOnlyPublicKey32, out var pubkey) || pubkey is null) return false;
            if (!SecpSchnorrSignature.TryCreate(signature64, out var signature) || signature is null) return false;
            return pubkey.SigVerifyBIP340(signature, messageHash32);
        }
        catch
        {
            return false;
        }
    }
}
