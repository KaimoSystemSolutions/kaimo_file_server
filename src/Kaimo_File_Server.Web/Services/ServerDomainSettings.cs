using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Notifications;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// The addresses (absolute base URLs, e.g. reverse-proxy URLs) the server is reachable under,
/// persisted as one JSON blob via <c>IConfigRepository</c> under <see cref="ConfigKey"/>. The
/// default address builds share-link URLs and the links in notification mails; access
/// restriction rules offer these entries as allowed hosts.
/// </summary>
public sealed class ServerDomainSettings
{
    public const string ConfigKey = "server.domains";
    public const int MaxAddresses = 10;

    /// <summary>Up to <see cref="MaxAddresses"/> absolute http(s) base URLs (no trailing slash).</summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>Index into <see cref="Addresses"/> of the default address.</summary>
    public int DefaultIndex { get; set; }

    /// <summary>The configured default address, or null when none is set.</summary>
    public string? DefaultAddress =>
        Addresses.Count == 0 ? null : Addresses[Math.Clamp(DefaultIndex, 0, Addresses.Count - 1)];

    /// <summary>
    /// Whether public links may be served under <paramref name="host"/> (the request host):
    /// only the default address's host, or any host when no default is set.
    /// </summary>
    public bool AllowsPublicLinkHost(string? host)
        => DefaultAddress is not { } address
           || string.Equals(HostOf(address), host, StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        Addresses = (Addresses ?? new())
            .Select(a => (a ?? string.Empty).Trim().TrimEnd('/'))
            .Where(IsValidAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAddresses)
            .ToList();

        DefaultIndex = Addresses.Count == 0 ? 0 : Math.Clamp(DefaultIndex, 0, Addresses.Count - 1);
    }

    /// <summary>Whether <paramref name="address"/> is an absolute http or https URL.</summary>
    public static bool IsValidAddress(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// The host name of an address (no scheme, no port), lower-case. Internationalized names are
    /// returned in Punycode, the form browsers send in the Host header.
    /// </summary>
    public static string HostOf(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri.IdnHost.ToLowerInvariant()
            : address.Trim().ToLowerInvariant();

    public static ServerDomainSettings Default() => new();

    /// <summary>
    /// Loads the normalized settings. Before the first save they are derived (not written)
    /// from the legacy share-link base addresses and the mail layout's public URL, which this
    /// setting replaces.
    /// </summary>
    public static async Task<ServerDomainSettings> LoadAsync(IConfigRepository config)
    {
        var stored = await config.GetAsync<ServerDomainSettings?>(ConfigKey, null);
        var result = stored is not null
            ? new ServerDomainSettings { Addresses = stored.Addresses.ToList(), DefaultIndex = stored.DefaultIndex }
            : FromLegacy(
                await config.GetAsync(ShareLinkSettings.ConfigKey, ShareLinkSettings.Default()),
                await config.GetAsync(MailLayoutSettings.ConfigKey, MailLayoutSettings.Default()));
        result.Normalize();
        return result;
    }

    internal static ServerDomainSettings FromLegacy(ShareLinkSettings shareLinks, MailLayoutSettings mailLayout)
    {
        var result = new ServerDomainSettings
        {
            Addresses = (shareLinks.BaseAddresses ?? new()).ToList(),
            DefaultIndex = shareLinks.DefaultIndex,
        };
        result.Normalize();

        var mailUrl = (mailLayout.PublicBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (IsValidAddress(mailUrl) && !result.Addresses.Contains(mailUrl, StringComparer.OrdinalIgnoreCase))
            result.Addresses.Add(mailUrl);
        return result;
    }
}
