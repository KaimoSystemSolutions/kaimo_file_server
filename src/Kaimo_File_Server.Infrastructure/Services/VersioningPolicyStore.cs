using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Live, database-backed <see cref="VersioningPolicy"/>. Every process that writes versions
/// (Web, SMB bridge, Host) holds one; values are re-read periodically so a change on the
/// settings page converges without a restart. Mirrors <c>BackupSettingsStore</c>.
/// </summary>
public sealed class VersioningPolicyStore(
    IServiceScopeFactory scopeFactory,
    ILogger<VersioningPolicyStore> logger) : IVersioningPolicyStore
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VersioningPolicy? _current;
    private DateTimeOffset _refreshAfter;

    public async Task<VersioningPolicy> GetAsync()
    {
        if (_current is not null && DateTimeOffset.UtcNow < _refreshAfter)
            return _current.Clone();

        await _gate.WaitAsync();
        try
        {
            if (_current is not null && DateTimeOffset.UtcNow < _refreshAfter)
                return _current.Clone();

            try
            {
                using var scope = scopeFactory.CreateScope();
                var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var loaded = await config.GetFreshAsync(VersioningPolicy.ConfigKey, VersioningPolicy.Default());
                loaded.Normalize();
                _current = loaded;
            }
            catch (Exception ex)
            {
                // Versioning must keep working while the database is briefly unreachable:
                // stay on the last known policy, or the defaults before the first load.
                logger.LogWarning(ex, "Could not load the versioning policy; using the last known values.");
                _current ??= VersioningPolicy.Default();
            }

            _refreshAfter = DateTimeOffset.UtcNow + RefreshInterval;
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(VersioningPolicy policy)
    {
        var normalized = policy.Clone();
        normalized.Normalize();

        using (var scope = scopeFactory.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            await config.SetAsync(VersioningPolicy.ConfigKey, normalized);
        }

        await _gate.WaitAsync();
        try
        {
            _current = normalized.Clone();
            _refreshAfter = DateTimeOffset.UtcNow + RefreshInterval;
        }
        finally
        {
            _gate.Release();
        }
    }
}
