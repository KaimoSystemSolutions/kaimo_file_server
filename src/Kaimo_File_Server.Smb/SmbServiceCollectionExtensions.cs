using Microsoft.Extensions.DependencyInjection;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Smb
{
    public static class SmbServiceCollectionExtensions
    {
        public static IServiceCollection AddSmb(this IServiceCollection services)
        {
            services.AddSingleton<SmbServer>(sp =>
                new SmbServer(sp, sp.GetRequiredService<IFileServiceFactory>()));
            return services;
        }
    }
}