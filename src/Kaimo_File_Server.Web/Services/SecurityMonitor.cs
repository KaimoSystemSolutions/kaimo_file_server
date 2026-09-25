using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Controllers.WebDav;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Collects login attempts (web, REST API, WebDAV) and API / WebDAV request counts per client
/// address, and writes them in batches through <see cref="ISecurityEventRepository"/>. Recording
/// never touches the database, so the login and request paths stay fast; requests are summed
/// into hourly buckets per address before they are stored.
/// </summary>
public sealed class SecurityMonitor(DemoModeOptions? demo = null, TimeProvider? timeProvider = null)
{
    /// <summary>Upper bound of unsaved attempts and buckets, e.g. while the database is down.</summary>
    public const int MaxPending = 10_000;

    private const int MaxUsernameLength = 256;
    private const int MaxAddressLength = 64;
    private const int MaxPathLength = 512;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentQueue<LoginAttemptRecord> _attempts = new();
    private readonly object _bucketLock = new();
    private Dictionary<(string Address, DateTime Hour), ClientActivityBucket> _buckets = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public void RecordLogin(SecurityChannel channel, string username, string? address, LoginResult result)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        Enqueue(new LoginAttemptRecord
        {
            AtUtc = now,
            Channel = channel,
            Username = Truncate((username ?? "").Trim().ToLowerInvariant(), MaxUsernameLength),
            Address = address is null ? null : Truncate(address.Trim(), MaxAddressLength),
            Outcome = result.Outcome,
            LockedUntilUtc = result.Outcome == LoginOutcome.LockedOut ? now + result.RetryAfter : null,
        });
    }

    public void RecordRequest(string? address, SecurityChannel channel, int statusCode, string path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        bool rejected = statusCode is StatusCodes.Status401Unauthorized
            or StatusCodes.Status403Forbidden
            or StatusCodes.Status429TooManyRequests;

        lock (_bucketLock)
            Merge(new ClientActivityBucket
            {
                Address = Truncate(string.IsNullOrWhiteSpace(address) ? "?" : address, MaxAddressLength),
                HourUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc),
                ApiRequests = channel == SecurityChannel.WebDav ? 0 : 1,
                WebDavRequests = channel == SecurityChannel.WebDav ? 1 : 0,
                RejectedRequests = rejected ? 1 : 0,
                LastSeenUtc = now,
                LastPath = Truncate(path, MaxPathLength),
            });
    }

    /// <summary>
    /// Writes everything recorded so far. Flushes are serialized; a failed batch is kept for the
    /// next flush (still bounded by <see cref="MaxPending"/>).
    /// </summary>
    public async Task FlushAsync(ISecurityEventRepository repository, CancellationToken ct = default)
    {
        await _flushGate.WaitAsync(ct);
        try
        {
            var attempts = new List<LoginAttemptRecord>();
            while (_attempts.TryDequeue(out var attempt))
                attempts.Add(attempt);

            List<ClientActivityBucket> buckets;
            lock (_bucketLock)
            {
                buckets = _buckets.Values.ToList();
                _buckets = new();
            }

            // The read-only demo persists nothing; the batch is discarded.
            if ((attempts.Count == 0 && buckets.Count == 0) || demo?.ReadOnly == true)
                return;

            try
            {
                await repository.SaveAsync(attempts, buckets, ct);
            }
            catch
            {
                // Re-queue copies: the failed context may have touched the originals' keys.
                foreach (var a in attempts)
                    Enqueue(new LoginAttemptRecord
                    {
                        AtUtc = a.AtUtc, Channel = a.Channel, Username = a.Username, Address = a.Address,
                        Outcome = a.Outcome, LockedUntilUtc = a.LockedUntilUtc,
                    });
                lock (_bucketLock)
                    foreach (var b in buckets)
                        Merge(new ClientActivityBucket
                        {
                            Address = b.Address, HourUtc = b.HourUtc, ApiRequests = b.ApiRequests,
                            WebDavRequests = b.WebDavRequests, RejectedRequests = b.RejectedRequests,
                            LastSeenUtc = b.LastSeenUtc, LastPath = b.LastPath,
                        });
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void Enqueue(LoginAttemptRecord attempt)
    {
        // ponytail: drops new attempts once MaxPending are unsaved (database down for a long time).
        if (_attempts.Count < MaxPending)
            _attempts.Enqueue(attempt);
    }

    /// <summary>Adds <paramref name="bucket"/> to the pending bucket of the same address and hour. Caller holds the lock.</summary>
    private void Merge(ClientActivityBucket bucket)
    {
        var key = (bucket.Address, bucket.HourUtc);
        if (!_buckets.TryGetValue(key, out var pending))
        {
            if (_buckets.Count < MaxPending)
                _buckets[key] = bucket;
            return;
        }

        pending.ApiRequests += bucket.ApiRequests;
        pending.WebDavRequests += bucket.WebDavRequests;
        pending.RejectedRequests += bucket.RejectedRequests;
        if (bucket.LastSeenUtc >= pending.LastSeenUtc)
        {
            pending.LastSeenUtc = bucket.LastSeenUtc;
            pending.LastPath = bucket.LastPath;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Writes the <see cref="SecurityMonitor"/> buffer to the database every few seconds (and once
/// more on shutdown) and prunes entries older than <c>Security:EventRetentionDays</c>.
/// </summary>
public sealed class SecurityMonitorFlushService(
    SecurityMonitor monitor,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SecurityMonitorFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>Retention of login attempts and client activity in days (default 365).</summary>
    public static int RetentionDays(IConfiguration configuration)
        => Math.Clamp(configuration.GetValue("Security:EventRetentionDays", 365), 1, 3650);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPrune = DateTime.UtcNow;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await FlushAsync(stoppingToken);

                if (DateTime.UtcNow >= nextPrune)
                {
                    nextPrune = DateTime.UtcNow + PruneInterval;
                    await PruneAsync();
                }

                await Task.Delay(FlushInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Keep what was recorded since the last tick.
            await FlushAsync(CancellationToken.None);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await monitor.FlushAsync(scope.ServiceProvider.GetRequiredService<ISecurityEventRepository>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Saving security events failed; retrying on the next flush.");
        }
    }

    private async Task PruneAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            int pruned = await scope.ServiceProvider.GetRequiredService<ISecurityEventRepository>()
                .PruneOlderThanAsync(DateTime.UtcNow.AddDays(-RetentionDays(configuration)));
            if (pruned > 0)
                logger.LogInformation("Security event retention pruned {Count} entries.", pruned);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Security event retention pruning failed.");
        }
    }
}

/// <summary>
/// Records every credential check in the <see cref="SecurityMonitor"/>. Wraps the real
/// login service, so the web login, the REST API, WebDAV and the own-password check are all
/// covered without touching their call sites. The transport is read from the current
/// request path; Blazor circuits have no API/WebDAV path and count as web.
/// </summary>
public sealed class MonitoredLoginService(
    ILoginService inner,
    SecurityMonitor monitor,
    IHttpContextAccessor http) : ILoginService
{
    public async Task<LoginResult> AuthenticateAsync(string username, string password, string? remoteAddress = null)
    {
        var result = await inner.AuthenticateAsync(username, password, remoteAddress);
        monitor.RecordLogin(SecurityMonitorMiddleware.ChannelOf(http.HttpContext?.Request.Path), username, remoteAddress, result);
        return result;
    }
}

/// <summary>Counts REST API and WebDAV requests per client address, including rejected ones.</summary>
public sealed class SecurityMonitorMiddleware(RequestDelegate next, SecurityMonitor monitor)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var channel = ChannelOf(path);
        if (channel == SecurityChannel.Web)
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            monitor.RecordRequest(context.Connection.RemoteIpAddress?.ToString(), channel,
                context.Response.StatusCode, path.Value ?? "");
        }
    }

    internal static SecurityChannel ChannelOf(PathString? path)
    {
        if (path is not { } p) return SecurityChannel.Web;
        if (p.StartsWithSegments("/api")) return SecurityChannel.Api;
        if (p.StartsWithSegments(WebDavPathResolver.Prefix)) return SecurityChannel.WebDav;
        return SecurityChannel.Web;
    }
}
