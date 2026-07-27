using System.Collections.Concurrent;
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
}
