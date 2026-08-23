using System.Security.Cryptography;
using System.Text;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// Computes the stable identity hash of a mutating request. A retry that reuses an
/// idempotency key is only replayed when its identity matches the stored one, so a
/// key can never mask a request for a <em>different</em> operation.
/// </summary>
public static class RequestFingerprint
{
    /// <summary>
    /// SHA-256 (lowercase hex) over the request's method, path and an
    /// operation-specific discriminator (e.g. the rename from/to, or the target
    /// path and content length for an upload). Parts are length-prefixed so no
    /// concatenation ambiguity can make two distinct requests collide.
    /// </summary>
    public static string Compute(string method, string path, params string?[] parts)
    {
        var sb = new StringBuilder();
        Append(sb, method);
        Append(sb, path);
        foreach (var part in parts)
            Append(sb, part ?? string.Empty);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void Append(StringBuilder sb, string value)
        => sb.Append(value.Length).Append(':').Append(value).Append('\n');
}

/// <summary>What to do when an idempotency key is presented.</summary>
public enum IdempotencyOutcome
{
    /// <summary>First time this key is seen — execute the operation and store its result.</summary>
    Proceed,

    /// <summary>Seen before with the same request — replay the stored result.</summary>
    Replay,

    /// <summary>Seen before with a <em>different</em> request — reject as a key conflict.</summary>
    Conflict,
}

/// <summary>Pure decision for the idempotency-key flow, isolated so it is unit-testable without a DB.</summary>
public static class IdempotencyDecision
{
    /// <param name="storedHash">The request hash of the existing receipt, or <c>null</c> if none exists.</param>
    /// <param name="incomingHash">The current request's fingerprint.</param>
    public static IdempotencyOutcome Decide(string? storedHash, string incomingHash)
    {
        if (storedHash is null)
            return IdempotencyOutcome.Proceed;
        return storedHash == incomingHash
            ? IdempotencyOutcome.Replay
            : IdempotencyOutcome.Conflict;
    }
}
