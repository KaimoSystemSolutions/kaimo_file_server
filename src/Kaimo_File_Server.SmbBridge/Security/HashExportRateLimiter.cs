using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.SmbBridge.Security;

public sealed class HashExportRateLimitOptions
{
    public const string SectionName = "ControlPlane:HashExportRateLimit";

    public int PermitLimit { get; set; } = 2;
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Small fixed-window limiter for the CA-constrained Samba workload identity.
/// No queue is used: excess exports fail closed.
/// </summary>
public sealed class HashExportRateLimiter
{
    private sealed class WindowState(DateTimeOffset started)
    {
        public DateTimeOffset Started { get; set; } = started;
        public int Count { get; set; }
    }

    private readonly ConcurrentDictionary<string, WindowState> _windows =
        new(StringComparer.Ordinal);
    private readonly HashExportRateLimitOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _continuationKey =
        RandomNumberGenerator.GetBytes(32);

    public HashExportRateLimiter(IOptions<HashExportRateLimitOptions> options)
        : this(options, TimeProvider.System)
    {
    }

    public HashExportRateLimiter(
        IOptions<HashExportRateLimitOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;

        if (_options.PermitLimit <= 0 || _options.WindowSeconds <= 0)
            throw new InvalidOperationException(
                "Hash-export rate-limit values must be greater than zero.");
    }

    public bool TryAcquire(string clientId, out TimeSpan retryAfter)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var state = _windows.GetOrAdd(clientId, _ => new WindowState(now));

        lock (state)
        {
            TimeSpan window = TimeSpan.FromSeconds(_options.WindowSeconds);
            if (now - state.Started >= window)
            {
                state.Started = now;
                state.Count = 0;
            }

            if (state.Count >= _options.PermitLimit)
            {
                retryAfter = window - (now - state.Started);
                return false;
            }

            state.Count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Creates a short-lived, client-bound token for exactly one next offset.
    /// This keeps continuation pages within the permit charged on page zero
    /// without allowing arbitrary offsets to bypass the export rate limit.
    /// </summary>
    public string CreateContinuationToken(string clientId, uint nextOffset)
    {
        Span<byte> payload = stackalloc byte[12];
        long expiresAt = _timeProvider.GetUtcNow()
            .AddSeconds(Math.Max(_options.WindowSeconds, 600))
            .ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt64BigEndian(payload, expiresAt);
        BinaryPrimitives.WriteUInt32BigEndian(payload[8..], nextOffset);

        byte[] clientBytes = Encoding.UTF8.GetBytes(clientId);
        byte[] signedData = new byte[payload.Length + clientBytes.Length];
        payload.CopyTo(signedData);
        clientBytes.CopyTo(signedData, payload.Length);
        byte[] signature = HMACSHA256.HashData(
            _continuationKey,
            signedData);
        byte[] token = new byte[payload.Length + signature.Length];
        payload.CopyTo(token);
        signature.CopyTo(token, payload.Length);
        return Convert.ToBase64String(token);
    }

    public bool IsValidContinuationToken(
        string clientId,
        uint expectedOffset,
        string token)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length != 44)
            return false;

        ReadOnlySpan<byte> payload = decoded.AsSpan(0, 12);
        long expiresAt = BinaryPrimitives.ReadInt64BigEndian(payload);
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        if (expiresAt < _timeProvider.GetUtcNow().ToUnixTimeSeconds()
            || offset != expectedOffset)
        {
            return false;
        }

        byte[] clientBytes = Encoding.UTF8.GetBytes(clientId);
        byte[] signedData = new byte[payload.Length + clientBytes.Length];
        payload.CopyTo(signedData);
        clientBytes.CopyTo(signedData, payload.Length);
        byte[] expectedSignature = HMACSHA256.HashData(
            _continuationKey,
            signedData);
        return CryptographicOperations.FixedTimeEquals(
            expectedSignature,
            decoded.AsSpan(payload.Length));
    }
}
