using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
                    configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage"));
            return services;
        }
    }
}