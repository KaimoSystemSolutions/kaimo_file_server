using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Smb
{
    public static class SmbServiceCollectionExtensions
    {
        public static IServiceCollection AddSmb(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<SmbServer>(sp =>
                new SmbServer(
                    sp,
                    sp.GetRequiredService<IFileServiceFactory>(),
                    sp.GetRequiredService<ILoggerFactory>(),
                    configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage"));

            // Expose SMB as a managed data service so the host reconciler can
            // start/stop it through the transport-agnostic abstraction.
            services.AddSingleton<IManagedDataService, SmbManagedDataService>();

            return services;
        }
    }
}