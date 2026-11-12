using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage.Dropbox;

/// <summary>Shared Dropbox OAuth endpoints and least-privilege runtime scopes.</summary>
public static class DropboxOAuthDefaults
{
    /// <summary>
    /// Public Dropbox application key shipped with Kaimo. A Dropbox app key is a
    /// public identifier, not a secret. Replace this with the key of your own
    /// registered Dropbox app, or override it with <c>ExternalStorage:Dropbox:AppKey</c>.
    /// PKCE is used so no application secret is required.
    /// </summary>
    public const string DefaultAppKey = "wovr28naznylgb7";

    public const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    public const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    /// <summary>Least-privilege scopes for browsing, sync, and virtual-share file access.</summary>
    public const string Scope =
        "account_info.read files.metadata.read files.content.read files.content.write";
}

/// <summary>
/// Resolves the public Dropbox application key used by PKCE authorization and
/// refresh-token exchanges. The default is Kaimo's shipped public app key; an
/// installation can supply its own key without a custom image.
/// </summary>
public sealed class DropboxIdentityConfiguration
{
    private DropboxIdentityConfiguration(string appKey) => AppKey = appKey;

    /// <summary>Public Dropbox application key. It is deliberately not a secret.</summary>
    public string AppKey { get; }

    /// <summary>True when an application key is available for authorization.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppKey);

    /// <summary>Reads the optional installation-local Dropbox application key.</summary>
    public static DropboxIdentityConfiguration FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var appKey = configuration["ExternalStorage:Dropbox:AppKey"];
        return Create(string.IsNullOrWhiteSpace(appKey) ? DropboxOAuthDefaults.DefaultAppKey : appKey);
    }

    /// <summary>
    /// Validates the application-key characters. An empty key is allowed so a
    /// deployment that does not use Dropbox still starts; authorization checks
    /// <see cref="IsConfigured"/> and fails with a clear message when it is unset.
    /// </summary>
    public static DropboxIdentityConfiguration Create(string? appKey)
    {
        var normalized = appKey?.Trim() ?? string.Empty;
        if (normalized.Length > 0 && !normalized.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException(
                "ExternalStorage:Dropbox:AppKey must be a Dropbox application key.");
        return new DropboxIdentityConfiguration(normalized);
    }
}
