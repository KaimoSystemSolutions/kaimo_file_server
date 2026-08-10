using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Services;

public sealed record CloudAccessRuntimeSettings
{
    public const string ConfigKey = "cloud_access.runtime";
    public const int DefaultDirectoryCacheSeconds = 20;
    public const int MinDirectoryCacheSeconds = 1;
    public const int MaxDirectoryCacheSeconds = 300;

    public int DirectoryCacheSeconds { get; set; } = DefaultDirectoryCacheSeconds;

    public void Normalize()
        => DirectoryCacheSeconds = Math.Clamp(
            DirectoryCacheSeconds,
            MinDirectoryCacheSeconds,
            MaxDirectoryCacheSeconds);

    public static CloudAccessRuntimeSettings Default() => new();
}

public interface ICloudAccessSettingsStore
{
    event Action? SettingsChanged;
    Task<CloudAccessRuntimeSettings> GetAsync();
    Task SetAsync(CloudAccessRuntimeSettings settings);
}

/// <summary>
/// Provides live, database-backed Cloud Access settings without consulting the
/// database for every directory navigation. Values are refreshed periodically
/// so multiple Web instances converge without a restart.
/// </summary>
public sealed class CloudAccessSettingsStore(IServiceScopeFactory scopeFactory)
    : ICloudAccessSettingsStore
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CloudAccessRuntimeSettings? _current;
    private DateTimeOffset _refreshAfter;

    public event Action? SettingsChanged;

    public async Task<CloudAccessRuntimeSettings> GetAsync()
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
            var loaded = await config.GetFreshAsync(
                CloudAccessRuntimeSettings.ConfigKey,
                CloudAccessRuntimeSettings.Default());
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

    public async Task SetAsync(CloudAccessRuntimeSettings settings)
    {
        var normalized = Copy(settings);
        normalized.Normalize();

        using var scope = scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(CloudAccessRuntimeSettings.ConfigKey, normalized);

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

    private static CloudAccessRuntimeSettings Copy(CloudAccessRuntimeSettings settings)
        => new() { DirectoryCacheSeconds = settings.DirectoryCacheSeconds };
}
