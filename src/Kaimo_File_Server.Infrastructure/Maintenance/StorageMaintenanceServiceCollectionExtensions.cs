using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Infrastructure.Maintenance;

public static class StorageMaintenanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Host-only storage-maintenance service and its options. The Host is
    /// the documented sole owner of storage-maintenance jobs; Web and SmbBridge must not
    /// register this.
    /// </summary>
    public static IServiceCollection AddStorageMaintenance(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageMaintenanceOptions>()
            .Bind(configuration.GetSection(StorageMaintenanceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageMaintenanceOptions>, StorageMaintenanceOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ShareTempArtifactSweeper>();
        services.AddHostedService<StorageMaintenanceService>();
        return services;
    }
}
