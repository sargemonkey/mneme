using System.Security.Cryptography;

namespace Mneme.Util;

/// <summary>
/// Minimal ULID generator (Crockford base32, 26 chars: 48-bit big-endian
/// millisecond timestamp + 80 bits of CSPRNG randomness). ULIDs are
/// lexicographically sortable by creation time, which makes them a good
/// idempotency key + ordering key for an append-only event log. Used for
/// library-generated event ids where the producer did not supply a stable id.
/// </summary>
public static class Ulid
{
    // Crockford base32 alphabet (excludes I, L, O, U to avoid ambiguity).
    private const string Encode = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Generate a ULID for the current UTC time.</summary>
    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    /// <summary>Generate a ULID whose time component is <paramref name="timestamp"/>.</summary>
    public static string NewUlid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        RandomNumberGenerator.Fill(bytes.Slice(6, 10));
        return Encode16(bytes);
    }

    // Canonical ULID Crockford encoding of a 16-byte value into 26 chars.
    private static string Encode16(ReadOnlySpan<byte> b)
    {
        Span<char> c = stackalloc char[26];
        c[0] = Encode[(b[0] & 224) >> 5];
        c[1] = Encode[b[0] & 31];
        c[2] = Encode[(b[1] & 248) >> 3];
        c[3] = Encode[((b[1] & 7) << 2) | ((b[2] & 192) >> 6)];
        c[4] = Encode[(b[2] & 62) >> 1];
        c[5] = Encode[((b[2] & 1) << 4) | ((b[3] & 240) >> 4)];
        c[6] = Encode[((b[3] & 15) << 1) | ((b[4] & 128) >> 7)];
        c[7] = Encode[(b[4] & 124) >> 2];
        c[8] = Encode[((b[4] & 3) << 3) | ((b[5] & 224) >> 5)];
        c[9] = Encode[b[5] & 31];
        c[10] = Encode[(b[6] & 248) >> 3];
        c[11] = Encode[((b[6] & 7) << 2) | ((b[7] & 192) >> 6)];
        c[12] = Encode[(b[7] & 62) >> 1];
        c[13] = Encode[((b[7] & 1) << 4) | ((b[8] & 240) >> 4)];
        c[14] = Encode[((b[8] & 15) << 1) | ((b[9] & 128) >> 7)];
        c[15] = Encode[(b[9] & 124) >> 2];
        c[16] = Encode[((b[9] & 3) << 3) | ((b[10] & 224) >> 5)];
        c[17] = Encode[b[10] & 31];
        c[18] = Encode[(b[11] & 248) >> 3];
        c[19] = Encode[((b[11] & 7) << 2) | ((b[12] & 192) >> 6)];
        c[20] = Encode[(b[12] & 62) >> 1];
        c[21] = Encode[((b[12] & 1) << 4) | ((b[13] & 240) >> 4)];
        c[22] = Encode[((b[13] & 15) << 1) | ((b[14] & 128) >> 7)];
        c[23] = Encode[(b[14] & 124) >> 2];
        c[24] = Encode[((b[14] & 3) << 3) | ((b[15] & 224) >> 5)];
        c[25] = Encode[b[15] & 31];
        return new string(c);
    }
}
