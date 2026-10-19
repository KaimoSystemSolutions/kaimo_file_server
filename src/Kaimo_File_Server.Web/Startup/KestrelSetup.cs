using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>Kestrel endpoints: plain HTTP (proxy/back-compat) + HTTPS terminated in-process. The HTTPS certificate is resolved per connection from HttpsCertificateProvider, so certificate renewal / replacement takes effect without a restart.</summary>
internal static class KestrelSetup
{
    public static void ConfigureKaimoKestrel(this WebApplicationBuilder builder)
    {
        // The aspnet base image sets ASPNETCORE_HTTP_PORTS=8080. The explicit endpoints below
        // already own the ports, so clear the implicit URL sources instead of letting Kestrel
        // log "Overriding address(es)" on every start.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, string.Empty);
        builder.WebHost.UseSetting(WebHostDefaults.HttpsPortsKey, string.Empty);

        builder.WebHost.ConfigureKestrel(options =>
        {
            var httpPort = builder.Configuration.GetValue("Kestrel:HttpPort", 8080);
            var httpsPort = builder.Configuration.GetValue("Kestrel:HttpsPort", 8443);

            options.ListenAnyIP(httpPort);
            options.ListenAnyIP(httpsPort, listen => listen.UseHttps(https =>
            {
                var provider = options.ApplicationServices
                    .GetRequiredService<HttpsCertificateProvider>();
                https.ServerCertificateSelector = (_, _) => provider.Current;
            }));
        });
    }
}
