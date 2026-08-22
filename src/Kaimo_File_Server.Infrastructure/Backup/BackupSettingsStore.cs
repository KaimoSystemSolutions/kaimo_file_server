using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Backup;

public interface IBackupSettingsStore
{
    event Action? SettingsChanged;
    Task<BackupSettings> GetAsync();
    Task SetAsync(BackupSettings settings);
}

/// <summary>
/// Live, database-backed backup settings. Values are refreshed periodically so
/// the Host scheduler and the Web UI converge without a restart. Mirrors
/// <c>CloudAccessSettingsStore</c>.
/// </summary>
public sealed class BackupSettingsStore(IServiceScopeFactory scopeFactory) : IBackupSettingsStore
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackupSettings? _current;
    private DateTimeOffset _refreshAfter;

    public event Action? SettingsChanged;

    public async Task<BackupSettings> GetAsync()
    {
        if (_current is not null && DateTimeOffset.UtcNow < _refreshAfter)
            return Copy(_current);

        await _gate.WaitAsync();
        try
        {
            if (_current is not null && DateTimeOffset.UtcNow < _refreshAfter)
                return Copy(_current);

            using var scope = scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            var loaded = await config.GetFreshAsync(BackupSettings.ConfigKey, BackupSettings.Default());
            loaded.Normalize();
            _current = Copy(loaded);
            _refreshAfter = DateTimeOffset.UtcNow + RefreshInterval;
            return Copy(loaded);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(BackupSettings settings)
    {
        var normalized = Copy(settings);
        normalized.Normalize();

        using var scope = scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(BackupSettings.ConfigKey, normalized);

        await _gate.WaitAsync();
        try
        {
            _current = Copy(normalized);
            _refreshAfter = DateTimeOffset.UtcNow + RefreshInterval;
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke();
    }

    private static BackupSettings Copy(BackupSettings settings) => new()
    {
        Enabled = settings.Enabled,
        WindowStart = settings.WindowStart,
        WindowEnd = settings.WindowEnd,
        RetentionCount = settings.RetentionCount,
        RetentionDays = settings.RetentionDays,
    };
}
