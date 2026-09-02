using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace BitchatWin.Services;

/// <summary>
/// Persists the 32-byte device seed every per-geohash identity is derived from.
///
/// The Swift client keeps this in the iOS keychain; the closest Windows
/// equivalent without dragging in a credential-manager dependency is DPAPI
/// under the current user, which is what this uses. Losing the seed just means
/// a new identity, not lost history.
/// </summary>
public sealed class IdentityStore
{
    private static readonly byte[] DpapiEntropy = "bitchat-win/device-seed/v1"u8.ToArray();

    private readonly string _seedPath;

    public IdentityStore(string? directory = null)
    {
        string dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "bitchat-win");
        Directory.CreateDirectory(dir);
        _seedPath = Path.Combine(dir, "device-seed.bin");
    }

    public string SeedPath => _seedPath;

    /// <summary>Loads the device seed, creating one on first run.</summary>
    public byte[] GetOrCreateDeviceSeed()
    {
        if (File.Exists(_seedPath))
        {
            try
            {
                byte[] stored = File.ReadAllBytes(_seedPath);
                byte[] seed = Unprotect(stored);
                if (seed.Length == 32) return seed;
            }
            catch
            {
                // Unreadable seed (copied profile, corrupt file): fall through and
                // mint a fresh one rather than leaving the app unusable.
            }
        }

        byte[] fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllBytes(_seedPath, Protect(fresh));
        }
        catch
        {
            // An unwritable profile means the identity is session-only, which is
            // still a working client.
        }
        return fresh;
    }

    /// <summary>Deletes the seed so the next start derives brand-new identities.</summary>
    public void Wipe()
    {
        try
        {
            if (File.Exists(_seedPath)) File.Delete(_seedPath);
        }
        catch
        {
            // Nothing useful to do; the caller is already discarding in-memory keys.
        }
    }

    private static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return ProtectWindows(data);
        return data;
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows()) return UnprotectWindows(data);
        return data;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) =>
        ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) =>
        ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser);
}
