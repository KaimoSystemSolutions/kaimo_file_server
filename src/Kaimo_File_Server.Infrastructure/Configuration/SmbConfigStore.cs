using Kaimo_File_Server.Core.Services.DataServices;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Configuration;

/// <summary>
/// Implements <see cref="ISmbConfigStore"/> over <see cref="IConfigRepository"/>.
/// Reads use <see cref="IConfigRepository.GetFreshAsync"/> because the flags are
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

    public async Task<SmbDialectConfig> GetDialectConfigAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        // Default true → backward compatible with the previously hard-coded
        // enableSMB2 = enableSMB3 = true behaviour.
        bool smb2 = await config.GetFreshAsync(SmbConfigKeys.Smb2EnabledKey, true);
        bool smb3 = await config.GetFreshAsync(SmbConfigKeys.Smb3EnabledKey, true);
        return new SmbDialectConfig(smb2, smb3);
    }
}
