using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Resolves the public Microsoft identity settings used by device-code grants
/// and refresh-token exchanges. The default is Kaimo's public client; an
/// installation can supply a tenant-owned public client without a custom image.
/// </summary>
public sealed class MicrosoftIdentityConfiguration
{
    private const string LoginHost = "login.microsoftonline.com";

    private MicrosoftIdentityConfiguration(string publicClientId, string authority)
    {
        PublicClientId = publicClientId;
        Authority = authority;
    }

    /// <summary>Public application identifier. It is deliberately not a secret.</summary>
    public string PublicClientId { get; }

    /// <summary>Tenant segment used below the Microsoft identity host.</summary>
    public string Authority { get; }

    public string DeviceCodeEndpoint => BuildEndpoint("devicecode");
    public string TokenEndpoint => BuildEndpoint("token");

    /// <summary>Creates the configuration from the documented Docker settings.</summary>
    public static MicrosoftIdentityConfiguration FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var publicClientId = configuration["ExternalStorage:Microsoft:PublicClientId"];
        var authority = configuration["ExternalStorage:Microsoft:Authority"];
        var hasPublicClientId = !string.IsNullOrWhiteSpace(publicClientId);
        var hasAuthority = !string.IsNullOrWhiteSpace(authority);
        if (hasPublicClientId != hasAuthority)
            throw new InvalidOperationException(
                "ExternalStorage:Microsoft:PublicClientId and ExternalStorage:Microsoft:Authority must be configured together.");
        return Create(
            hasPublicClientId ? publicClientId! : OneDriveOAuthDefaults.ClientId,
            hasAuthority ? authority! : OneDriveOAuthDefaults.Authority);
    }

    /// <summary>Validates a public-client configuration before any token is requested.</summary>
    public static MicrosoftIdentityConfiguration Create(string publicClientId, string authority)
    {
        if (!Guid.TryParse(publicClientId, out _))
            throw new InvalidOperationException("ExternalStorage:Microsoft:PublicClientId must be a Microsoft application GUID.");

        var normalizedAuthority = authority?.Trim().Trim('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedAuthority)
            || normalizedAuthority.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '.')))
            throw new InvalidOperationException("ExternalStorage:Microsoft:Authority must be a tenant identifier, domain, or supported authority alias.");

        return new MicrosoftIdentityConfiguration(publicClientId, normalizedAuthority);
    }

    private string BuildEndpoint(string operation)
        => $"https://{LoginHost}/{Authority}/oauth2/v2.0/{operation}";
}
