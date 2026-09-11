using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Config keys and a short-cached snapshot of the WebDAV service settings.
///
/// The WebDAV service runs in the Web process, so — unlike SMB — there is no
/// reconciler round trip: this type reads the desired-state flag directly from
/// <see cref="IConfigRepository"/> (with a five-second cache so a directory
/// listing's burst of requests does not hammer the config store) and the
/// settings page writes the reported status key when the toggle is saved.
///
/// Registered as a singleton; because <see cref="IConfigRepository"/> is scoped,
/// each refresh opens a short-lived scope to read it.
/// </summary>
public sealed class WebDavOptions
{
    /// <summary>Service key following the <see cref="DataServiceKeys"/> convention.</summary>
    public const string ServiceKey = "webdav";

    /// <summary>Bool flag: whether the WebDAV service should accept requests.</summary>
    public static string EnabledKey => DataServiceKeys.EnabledKey(ServiceKey);

    /// <summary>String: last reported <see cref="DataServiceStatus"/>.</summary>
    public static string StatusKey => DataServiceKeys.StatusKey(ServiceKey);

    /// <summary>Bool flag: refuse HTTP Basic on a plain-HTTP request.</summary>
    public const string RequireHttpsKey = "services.webdav.requireHttps";

    /// <summary>Int: default lock duration in seconds.</summary>
    public const string LockTimeoutSecondsKey = "services.webdav.lockTimeoutSeconds";

    public const int DefaultLockTimeoutSeconds = 300;
    public const int MaxLockTimeoutSeconds = 3600;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _cached;
    private DateTimeOffset _readAt;

    public WebDavOptions(IServiceScopeFactory scopes, TimeProvider clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    /// <summary>Immutable view of the WebDAV settings at one point in time.</summary>
    public sealed record Snapshot(bool Enabled, bool RequireHttps, int LockTimeoutSeconds);

    /// <summary>Returns the current settings, reading from the config store at most every five seconds.</summary>
    public async Task<Snapshot> GetAsync()
    {
        if (TryGetFresh(out var cached))
            return cached;

        await _gate.WaitAsync();
        try
        {
            if (TryGetFresh(out cached))
                return cached;

            await using var scope = _scopes.CreateAsyncScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

            var enabled = await config.GetBoolAsync(EnabledKey, false);
            var requireHttps = await config.GetBoolAsync(RequireHttpsKey, true);
            var lockTimeout = Math.Clamp(
                await config.GetIntAsync(LockTimeoutSecondsKey, DefaultLockTimeoutSeconds),
                1, MaxLockTimeoutSeconds);

            _cached = new Snapshot(enabled, requireHttps, lockTimeout);
            _readAt = _clock.GetUtcNow();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(out Snapshot snapshot)
    {
        if (_cached is { } c && _clock.GetUtcNow() - _readAt < CacheTtl)
        {
            snapshot = c;
            return true;
        }

        snapshot = null!;
        return false;
    }
}
