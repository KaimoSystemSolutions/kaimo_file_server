using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238) with the parameters every authenticator app
/// supports: HMAC-SHA1, 6 digits, 30-second steps. A code is accepted for one step of clock
/// drift in either direction (90 s in total), and never for a step at or before the last one
/// used, so a code cannot be replayed within its window.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int AllowedDriftSteps = 1;

    /// <summary>Secret length recommended by RFC 4226 (160 bits).</summary>
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code of one time step (HOTP, RFC 4226, with the step as counter).</summary>
    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        int offset = hash[^1] & 0x0F;
        int binary = (BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF) % 1_000_000;
        return binary.ToString("D6");
    }

    /// <summary>
    /// Checks <paramref name="code"/> against the steps around <paramref name="now"/>. Returns the
    /// matching step, which the caller must store as the new last used step, or <c>null</c>.
    /// Every candidate is compared in constant time, whether or not an earlier one matched.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized is null)
            return null;

        var given = Encoding.ASCII.GetBytes(normalized);
        long current = StepAt(now);
        long? match = null;
        for (long step = current - AllowedDriftSteps; step <= current + AllowedDriftSteps; step++)
        {
            bool equal = CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(Compute(secret, step)));
            if (equal && step > lastUsedStep)
                match = step;
        }
        return match;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI authenticator apps read from the enrollment QR code. Issuer and
    /// account are percent-encoded; the secret is base32 without padding.
    /// </summary>
    public static string ProvisioningUri(byte[] secret, string issuer, string account)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{encodedIssuer}:{Uri.EscapeDataString(account)}" +
               $"?secret={Base32.Encode(secret)}&issuer={encodedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>The code without spaces and dashes, or <c>null</c> unless it is exactly six ASCII digits.</summary>
    private static string? Normalize(string? code)
    {
        if (code is null)
            return null;
        var stripped = code.Replace(" ", "").Replace("-", "");
        return stripped.Length == Digits && stripped.All(char.IsAsciiDigit) ? stripped : null;
    }
}
