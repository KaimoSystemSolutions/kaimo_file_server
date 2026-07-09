using Kaimo_File_Server.Core.Services.DataServices;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>
/// Implements <see cref="ISmbConfigStore"/> over <see cref="IConfigRepository"/>.
/// Reads use <see cref="IConfigRepository.GetFreshAsync"/> because the settings are
/// written in the Web process but read in the SMB host process — a cached value
/// would hide the change for up to the cache TTL. Mirrors <see cref="SearchConfigStore"/>.
/// </summary>
public sealed class SmbConfigStore : ISmbConfigStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SmbConfigStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<SmbProtocolSettings> GetProtocolSettingsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        // Fallback = library-default settings → backward compatible with the
        // previously hard-coded dialect range / signing behaviour.
        var settings = await config.GetFreshAsync(
            SmbProtocolSettings.ConfigKey, SmbProtocolSettings.Default());

        // A hand-edited or partially-deserialized object could carry an inverted
        // range; guarantee the host always receives a sane one.
        settings.Normalize();
        return settings;
    }

    public async Task<Guid> GetOrCreateServerGuidAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        // Fresh read: the value is written once (here) but may already exist from a
        // previous run in another process, so bypass the cache to avoid re-minting it.
        var raw = await config.GetFreshAsync(SmbConfigKeys.ServerGuidKey, string.Empty);
        if (Guid.TryParse(raw, out var existing) && existing != Guid.Empty)
            return existing;

        var generated = Guid.NewGuid();
        await config.SetAsync(SmbConfigKeys.ServerGuidKey, generated.ToString());
        return generated;
    }
}
