using Google.Apis.Drive.v3;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>Google Drive capability profiles exposed by Kaimo.</summary>
public enum GoogleDriveScopeProfile
{
    SelectedItems,
    ReadOnly,
    ReadWrite
}

/// <summary>
/// Validated installation-local Google identity settings. Secret values are
/// loaded from a protected file when configured and are never represented by
/// a tracked configuration default.
/// </summary>
public sealed class GoogleIdentityConfiguration
{
    private const int MaximumSecretFileBytes = 1024 * 1024;

    private GoogleIdentityConfiguration(
        string? clientId,
        string? clientSecret,
        Uri? externalBaseUri,
        GoogleDriveScopeProfile defaultScopeProfile,
        string? workspaceCredentialJson,
        string? workspaceImpersonatedSubject)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        ExternalBaseUri = externalBaseUri;
        DefaultScopeProfile = defaultScopeProfile;
        WorkspaceCredentialJson = workspaceCredentialJson;
        WorkspaceImpersonatedSubject = workspaceImpersonatedSubject;
    }

    public string? ClientId { get; }
    internal string? ClientSecret { get; }
    public Uri? ExternalBaseUri { get; }
    public GoogleDriveScopeProfile DefaultScopeProfile { get; }
    internal string? WorkspaceCredentialJson { get; }
    public string? WorkspaceImpersonatedSubject { get; }
    public bool DelegatedOAuthEnabled => ClientId is not null && ClientSecret is not null;
    public bool WorkspaceIdentityEnabled => WorkspaceCredentialJson is not null;

    /// <summary>The exact callback URI that must be registered in Google Cloud.</summary>
    public Uri GetCallbackUri()
        => ExternalBaseUri is null
            ? throw new InvalidOperationException(
                "ExternalStorage:Google:ExternalBaseUrl is required for delegated Google authorization.")
            : new Uri(ExternalBaseUri, "api/google/callback");

    /// <summary>Returns the least-privilege Google Drive scopes for a profile.</summary>
    public static IReadOnlyList<string> GetScopes(GoogleDriveScopeProfile profile)
        => profile switch
        {
            GoogleDriveScopeProfile.SelectedItems => [DriveService.Scope.DriveFile],
            GoogleDriveScopeProfile.ReadOnly => [DriveService.Scope.DriveReadonly],
            GoogleDriveScopeProfile.ReadWrite => [DriveService.Scope.Drive],
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };

    /// <summary>Loads and validates Google identity configuration.</summary>
    public static GoogleIdentityConfiguration FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("ExternalStorage:Google");
        var legacy = configuration.GetSection("GoogleOAuth");
        var clientId = Optional(section["ClientId"]) ?? Optional(legacy["ClientId"]);
        var directSecret = Optional(section["ClientSecret"]) ?? Optional(legacy["ClientSecret"]);
        var secretFile = Optional(section["ClientSecretFile"]);

        if (directSecret is not null && secretFile is not null)
            throw new InvalidOperationException(
                "Configure either ExternalStorage:Google:ClientSecret or ClientSecretFile, not both.");

        var clientSecret = secretFile is null
            ? directSecret
            : ReadSecretFile(secretFile, "Google OAuth client secret");

        if ((clientId is null) != (clientSecret is null))
            throw new InvalidOperationException(
                "Google delegated OAuth requires both a client ID and a client secret.");
        if (clientId is not null
            && (clientId.Length > 512 || clientId.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("The Google OAuth client ID is invalid.");

        Uri? externalBaseUri = null;
        var externalBaseUrl = Optional(section["ExternalBaseUrl"]);
        if (externalBaseUrl is not null)
        {
            if (!Uri.TryCreate(externalBaseUrl, UriKind.Absolute, out externalBaseUri)
                || (externalBaseUri.Scheme != Uri.UriSchemeHttps
                    && !IsLoopbackDevelopmentUri(externalBaseUri))
                || !string.IsNullOrEmpty(externalBaseUri.UserInfo)
                || !string.IsNullOrEmpty(externalBaseUri.Query)
                || !string.IsNullOrEmpty(externalBaseUri.Fragment))
                throw new InvalidOperationException(
                    "ExternalStorage:Google:ExternalBaseUrl must be an HTTPS URL or a loopback HTTP development URL.");

            externalBaseUri = new Uri(externalBaseUri.AbsoluteUri.TrimEnd('/') + "/");
        }

        if (clientId is not null && externalBaseUri is null)
            throw new InvalidOperationException(
                "ExternalStorage:Google:ExternalBaseUrl is required when delegated Google OAuth is configured.");

        var defaultScopeProfile = ParseScopeProfile(section["DefaultScopeProfile"]);
        var workspaceCredentialFile = Optional(section["Workspace:CredentialFile"]);
        var workspaceCredentialJson = workspaceCredentialFile is null
            ? null
            : ReadSecretFile(workspaceCredentialFile, "Google Workspace credential");
        var impersonatedSubject = Optional(section["Workspace:ImpersonatedSubject"]);

        if (impersonatedSubject is not null && workspaceCredentialJson is null)
            throw new InvalidOperationException(
                "A Google Workspace impersonated subject requires ExternalStorage:Google:Workspace:CredentialFile.");
        if (workspaceCredentialJson is not null)
            ValidateWorkspaceCredential(workspaceCredentialJson);
        if (impersonatedSubject is not null
            && (impersonatedSubject.Contains(' ') || !impersonatedSubject.Contains('@')))
            throw new InvalidOperationException(
                "The Google Workspace impersonated subject must be an email address.");

        return new GoogleIdentityConfiguration(
            clientId,
            clientSecret,
            externalBaseUri,
            defaultScopeProfile,
            workspaceCredentialJson,
            impersonatedSubject);
    }

    public static GoogleDriveScopeProfile ParseScopeProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GoogleDriveScopeProfile.ReadWrite;

        return value.Trim().ToLowerInvariant() switch
        {
            "selecteditems" or "selected-items" => GoogleDriveScopeProfile.SelectedItems,
            "readonly" or "read-only" => GoogleDriveScopeProfile.ReadOnly,
            "readwrite" or "read-write" => GoogleDriveScopeProfile.ReadWrite,
            _ => throw new InvalidOperationException(
                "Google scope profile must be SelectedItems, ReadOnly, or ReadWrite.")
        };
    }

    private static string ReadSecretFile(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"The {description} file path must be absolute.");

        var file = new FileInfo(path);
        if (!file.Exists)
            throw new InvalidOperationException($"The configured {description} file does not exist.");
        if (file.Length == 0 || file.Length > MaximumSecretFileBytes)
            throw new InvalidOperationException($"The configured {description} file has an invalid size.");

        var value = File.ReadAllText(file.FullName).Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"The configured {description} file is empty.");
        return value;
    }

    private static bool IsLoopbackDevelopmentUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;

    private static void ValidateWorkspaceCredential(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasString(root, "type", "service_account")
                || !HasString(root, "client_email")
                || !HasString(root, "private_key"))
                throw new InvalidOperationException(
                    "The Google Workspace credential file must contain a service-account credential.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The Google Workspace credential file is not valid JSON.", exception);
        }
    }

    private static bool HasString(JsonElement element, string name, string? expected = null)
        => element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(property.GetString())
           && (expected is null || string.Equals(property.GetString(), expected, StringComparison.Ordinal));

    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
