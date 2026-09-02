using System;
using System.Text;

namespace BitchatWin.Protocol;

/// <summary>
/// Base32 geohash encoder/decoder. Ported from bitchat's <c>Geohash.swift</c>
/// so that channel names, relay selection and bounds all agree bit-for-bit
/// with the iOS/Android clients.
/// </summary>
public static class Geohash
{
    private const string Base32Chars = "0123456789bcdefghjkmnpqrstuvwxyz";
    private static readonly int[] BitMasks = { 16, 8, 4, 2, 1 };

    private static int Base32Value(char c)
    {
        int index = Base32Chars.IndexOf(char.ToLowerInvariant(c));
        return index;
    }

    /// <summary>True for a non-empty base32 geohash of at most 12 characters.</summary>
    public static bool IsValid(string? geohash)
    {
        if (string.IsNullOrEmpty(geohash) || geohash.Length > 12) return false;
        foreach (char c in geohash)
        {
            if (Base32Value(c) < 0) return false;
        }
        return true;
    }

    /// <summary>Encodes a coordinate into a geohash of the requested precision.</summary>
    public static string Encode(double latitude, double longitude, int precision)
    {
        if (precision <= 0) return string.Empty;

        double latMin = -90.0, latMax = 90.0;
        double lonMin = -180.0, lonMax = 180.0;

        bool isEven = true;
        int bit = 0;
        int ch = 0;
        var result = new StringBuilder(precision);

        double lat = Math.Clamp(latitude, -90.0, 90.0);
        double lon = Math.Clamp(longitude, -180.0, 180.0);

        while (result.Length < precision)
        {
            if (isEven)
            {
                double mid = (lonMin + lonMax) / 2;
                if (lon >= mid) { ch |= 1 << (4 - bit); lonMin = mid; }
                else { lonMax = mid; }
            }
            else
            {
                double mid = (latMin + latMax) / 2;
                if (lat >= mid) { ch |= 1 << (4 - bit); latMin = mid; }
                else { latMax = mid; }
            }

            isEven = !isEven;
            if (bit < 4)
            {
                bit++;
            }
            else
            {
                result.Append(Base32Chars[ch]);
                bit = 0;
                ch = 0;
            }
        }

        return result.ToString();
    }

    /// <summary>Decodes a geohash to the centre of its bounding box.</summary>
    public static (double Lat, double Lon) DecodeCenter(string geohash)
    {
        var (latMin, latMax, lonMin, lonMax) = DecodeBounds(geohash);
        return ((latMin + latMax) / 2, (lonMin + lonMax) / 2);
    }

    /// <summary>Decodes a geohash to its latitude/longitude bounds.</summary>
    public static (double LatMin, double LatMax, double LonMin, double LonMax) DecodeBounds(string geohash)
    {
        double latMin = -90.0, latMax = 90.0;
        double lonMin = -180.0, lonMax = 180.0;

        bool isEven = true;
        foreach (char c in geohash)
        {
            int cd = Base32Value(c);
            if (cd < 0) continue;

            foreach (int mask in BitMasks)
            {
                if (isEven)
                {
                    double mid = (lonMin + lonMax) / 2;
                    if ((cd & mask) != 0) lonMin = mid; else lonMax = mid;
                }
                else
                {
                    double mid = (latMin + latMax) / 2;
                    if ((cd & mask) != 0) latMin = mid; else latMax = mid;
                }
                isEven = !isEven;
            }
        }

        return (latMin, latMax, lonMin, lonMax);
    }
}
