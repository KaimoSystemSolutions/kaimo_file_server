using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Web.Controllers.WebDav;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Collects login attempts (web, REST API, WebDAV) and API / WebDAV request counts per client
/// address, and writes them in batches through <see cref="ISecurityEventRepository"/>. Recording
/// never touches the database, so the login and request paths stay fast; requests are summed
/// into hourly buckets per address before they are stored, and repeated LockedOut attempts of one
/// lock are folded into one row (<see cref="LockoutRepeatFolder"/>).
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
    // Only touched inside the flush gate.
    private readonly LockoutRepeatFolder _repeats = new();

    public void RecordLogin(SecurityChannel channel, string username, string? address, LoginResult result)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        bool locked = result.Outcome == LoginOutcome.LockedOut;
        // A locked address refuses every user name; it is logged without one, so the
        // overview neither shows the names tried as locked accounts nor splits its repeats.
        bool addressLocked = locked && result.LockScope == LockoutScope.Address;
        Enqueue(new LoginAttemptRecord
        {
            AtUtc = now,
            Channel = channel,
            Username = addressLocked ? "" : Truncate((username ?? "").Trim().ToLowerInvariant(), MaxUsernameLength),
            Address = address is null ? null : Truncate(address.Trim(), MaxAddressLength),
            Outcome = addressLocked ? LoginOutcome.AddressLockedOut : result.Outcome,
            LockedUntilUtc = locked ? now + result.RetryAfter : null,
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
    /// next flush (still bounded by <see cref="MaxPending"/>), and so are repeat counts that could
    /// not be added to their saved rows.
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
            if (demo?.ReadOnly == true)
                return;

            var rows = _repeats.Fold(attempts);
            if (rows.Count > 0 || buckets.Count > 0)
            {
                await SaveBatchAsync(repository, rows, buckets, ct);
                _repeats.BatchSaved(_time.GetUtcNow().UtcDateTime);
            }

            if (_repeats.PendingRepeats.Count > 0)
            {
                // A failure keeps the increments for the next flush; nothing is counted twice.
                await repository.AddLockoutRepeatsAsync(new Dictionary<long, int>(_repeats.PendingRepeats), ct);
                _repeats.RepeatsSaved();
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task SaveBatchAsync(
        ISecurityEventRepository repository, List<LoginAttemptRecord> rows, List<ClientActivityBucket> buckets,
        CancellationToken ct)
    {
        try
        {
            await repository.SaveAsync(rows, buckets, ct);
        }
        catch
        {
            _repeats.BatchFailed();
            // Re-queue copies: the failed context may have touched the originals' keys.
            foreach (var a in rows)
                Enqueue(new LoginAttemptRecord
                {
                    AtUtc = a.AtUtc, Channel = a.Channel, Username = a.Username, Address = a.Address,
                    Outcome = a.Outcome, LockedUntilUtc = a.LockedUntilUtc, RepeatCount = a.RepeatCount,
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
        catch (Core.Security.ReadOnlyDemoException)
        {
            // Read-only demo: nothing is recorded, so there is nothing to prune.
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
/// A lockout raises a notification, once per lock: <c>security.account_locked</c> for an
/// account, <c>security.address_locked</c> for a client address.
/// </summary>
public sealed class MonitoredLoginService(
    ILoginService inner,
    SecurityMonitor monitor,
    IHttpContextAccessor http,
    INotificationPublisher? notifications = null,
    IUserRepository? users = null) : ILoginService
{
    // Every attempt during a lock returns LockedOut again; publish only the first one per lock.
    // ponytail: in-memory per process and cleared wholesale at the cap; the dispatcher's
    // throttle is the real flood guard for the mails themselves.
    private static readonly ConcurrentDictionary<string, DateTime> LockNotifiedUntil = new(StringComparer.OrdinalIgnoreCase);

    public async Task<LoginResult> AuthenticateAsync(string username, string password, string? remoteAddress = null, string? host = null)
    {
        var result = await inner.AuthenticateAsync(username, password, remoteAddress, host);
        var channel = SecurityMonitorMiddleware.ChannelOf(http.HttpContext?.Request.Path);
        monitor.RecordLogin(channel, username, remoteAddress, result);
        if (result.Outcome == LoginOutcome.LockedOut && notifications is not null)
        {
            // A notification must never affect the login result (the publisher itself never throws).
            try
            {
                // A locked address locks no account: notifying (and looking up) every user name
                // tried from it would tell real users their account is locked when it is not.
                if (result.LockScope == LockoutScope.Address)
                    await NotifyAddressLockedAsync(remoteAddress, channel, result.RetryAfter);
                else
                    await NotifyLockedAsync(username, remoteAddress, result.RetryAfter);
            }
            catch (Exception) { /* user lookup failed; the lock itself is already in effect */ }
        }
        return result;
    }

    private async Task NotifyLockedAsync(string username, string? remoteAddress, TimeSpan retryAfter)
    {
        var key = username.Trim();
        if (!IsFirstOfLock(key, retryAfter)) return;

        // Unknown user names still notify the security administrators, just without an affected user.
        var user = users is null ? null : await users.GetByUsernameAsync(key);
        await notifications!.PublishAsync(NotificationEvents.AccountLocked(user?.Id, key, remoteAddress, retryAfter));
    }

    private async Task NotifyAddressLockedAsync(string? remoteAddress, SecurityChannel channel, TimeSpan retryAfter)
    {
        // An address lock needs a known address; without one there is nothing to report.
        var address = remoteAddress?.Trim();
        if (string.IsNullOrEmpty(address)) return;
        // Leading "|" cannot start a user name, so this never collides with an account key.
        if (!IsFirstOfLock("|ip|" + address, retryAfter)) return;

        await notifications!.PublishAsync(NotificationEvents.AddressLocked(address, channel, retryAfter));
    }

    /// <summary>True for the first LockedOut of a lock of <paramref name="key"/>; remembers the lock until it ends.</summary>
    private static bool IsFirstOfLock(string key, TimeSpan retryAfter)
    {
        var now = DateTime.UtcNow;
        if (LockNotifiedUntil.TryGetValue(key, out var until) && until > now) return false;
        if (LockNotifiedUntil.Count > 10_000) LockNotifiedUntil.Clear();
        LockNotifiedUntil[key] = now + retryAfter;
        return true;
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
