using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Smb
{
    public static class SmbServiceCollectionExtensions
    {
        public static IServiceCollection AddSmb(this IServiceCollection services)
        {
            services.AddSingleton<SmbServer>();
            return services;
        }
    }
}
