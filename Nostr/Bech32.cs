using System;
using System.Collections.Generic;
using System.Text;

namespace BitchatWin.Nostr;

/// <summary>Bech32 (BIP-173) encoder, used for the <c>npub</c> display form.</summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public static string Encode(string hrp, ReadOnlySpan<byte> data)
    {
        var converted = ConvertBits(data, 8, 5, pad: true)
            ?? throw new ArgumentException("Cannot convert data to base32", nameof(data));

        var checksum = CreateChecksum(hrp, converted);
        var sb = new StringBuilder(hrp).Append('1');
        foreach (byte b in converted) sb.Append(Charset[b]);
        foreach (byte b in checksum) sb.Append(Charset[b]);
        return sb.ToString();
    }

    private static List<byte>? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        int acc = 0, bits = 0;
        int maxValue = (1 << toBits) - 1;
        var result = new List<byte>();

        foreach (byte value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxValue));
            }
        }

        if (pad)
        {
            if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxValue));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxValue) != 0)
        {
            return null;
        }

        return result;
    }

    private static List<byte> CreateChecksum(string hrp, List<byte> data)
    {
        var values = new List<byte>();
        values.AddRange(HrpExpand(hrp));
        values.AddRange(data);
        values.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });

        uint polymod = Polymod(values) ^ 1;
        var checksum = new List<byte>(6);
        for (int i = 0; i < 6; i++) checksum.Add((byte)((polymod >> (5 * (5 - i))) & 31));
        return checksum;
    }

    private static List<byte> HrpExpand(string hrp)
    {
        var result = new List<byte>(hrp.Length * 2 + 1);
        foreach (char c in hrp) result.Add((byte)(c >> 5));
        result.Add(0);
        foreach (char c in hrp) result.Add((byte)(c & 31));
        return result;
    }

    private static uint Polymod(List<byte> values)
    {
        uint[] generator = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
        uint checksum = 1;
        foreach (byte value in values)
        {
            uint top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ value;
            for (int i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0) checksum ^= generator[i];
            }
        }
        return checksum;
    }
}
