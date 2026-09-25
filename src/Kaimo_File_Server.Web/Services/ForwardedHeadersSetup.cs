using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Decides which peers may set <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c>.
/// The client address feeds the per-(user, address) login lockout and the
/// scheme feeds the WebDAV HTTPS requirement, so a header from an untrusted
/// peer must be ignored rather than believed.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Used when <c>ForwardedHeaders:KnownNetworks</c> and <c>KnownProxies</c>
    /// are both empty: loopback only. Private ranges are deliberately not trusted
    /// by default — every machine in the LAN (and, with Docker's userland proxy,
    /// every published-port connection via the bridge gateway) comes from one, so
    /// trusting them would let any client forge its address. A reverse proxy in
    /// the Docker network or LAN must be configured explicitly.
    /// </summary>
    public static readonly string[] DefaultKnownNetworks = ["127.0.0.0/8", "::1/128"];

    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        string[] networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        string[] proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        if (networks.Length == 0 && proxies.Length == 0)
            networks = DefaultKnownNetworks;

        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
        // Only the entry appended by the nearest trusted proxy is used.
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (string network in networks)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network.Trim()));
        foreach (string proxy in proxies)
            options.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
    }
}
