using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TaskFlow.Infrastructure.Identity;

// RFC 4226 / 6238: HMAC-SHA1, 30-second period, six digits, +/- one period.
// No external services or QR APIs ever receive the shared secret.
public static class MfaCodes
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(20));
    private static string Encode(byte[] bytes)
    {
        var output = new StringBuilder(); int bits = 0, buffer = 0;
        foreach (var b in bytes) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { bits -= 5; output.Append(Alphabet[(buffer >> bits) & 31]); } }
        return output.ToString();
    }
    private static byte[] Decode(string value)
    {
        var output = new List<byte>(); int bits = 0, buffer = 0;
        foreach (var c in value) { var n = Alphabet.IndexOf(c); if (n < 0) throw new FormatException("Invalid key."); buffer = (buffer << 5) | n; bits += 5; if (bits >= 8) { bits -= 8; output.Add((byte)(buffer >> bits)); } }
        return output.ToArray();
    }
    public static string At(string secret, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var key = Decode(secret);
        try { var mac = HMACSHA1.HashData(key, counter); var offset = mac[^1] & 15; var number = BinaryPrimitives.ReadInt32BigEndian(mac.AsSpan(offset, 4)) & int.MaxValue; return (number % (digits == 8 ? 100000000 : 1000000)).ToString(digits == 8 ? "D8" : "D6", CultureInfo.InvariantCulture); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static long? Match(string secret, string? code, DateTimeOffset now, long last)
    {
        if (code is null || code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var step = now.ToUnixTimeSeconds() / 30; long? found = null;
        for (var candidate = step - 1; candidate <= step + 1; candidate++)
            if (candidate > last && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(At(secret, candidate)))) found = candidate;
        return found;
    }
    public static string NewRecoveryCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public static string RecoveryHash(long user, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToString(CultureInfo.InvariantCulture) + ":" + value.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant())));
}
