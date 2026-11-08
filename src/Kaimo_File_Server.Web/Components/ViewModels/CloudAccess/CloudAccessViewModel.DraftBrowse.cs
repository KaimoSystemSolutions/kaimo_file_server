using System.Text.Json;
using System.Text.Json.Nodes;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>What the connection form knows when its remote root is picked.</summary>
public sealed record ConnectionFolderBrowseRequest(
    Guid? ExistingConnectionId,
    string ProviderId,
    StorageAuthorizationMode Mode,
    string SettingsJson,
    string Path,
    string? Username = null,
    string? Password = null,
    byte[]? SshPrivateKey = null);

public partial class CloudAccessViewModel
{
    /// <summary>
    /// Lists folders with the settings currently in the SSH connection form. The connection
    /// used here lives only for this call and is never saved. A stored password follows the
    /// same rule as saving: it is not sent to a changed target.
    /// </summary>
    public async Task<List<CloudDirectoryItem>> ListDraftConnectionFoldersAsync(ConnectionFolderBrowseRequest request)
    {
        if (_providerCatalog is null || _directoryTargets is null || _credentialVault is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        StorageConnection? stored = request.ExistingConnectionId is Guid id ? await GetManagedConnectionAsync(id) : null;
        if (stored is null)
            await EnsureCanManageConnectionsAsync();

        string providerId = stored?.ProviderId ?? request.ProviderId;
        StorageAuthorizationMode mode = stored?.AuthorizationMode ?? request.Mode;
        if (providerId is not ("sftp" or "rsync-ssh")
            || mode is not (StorageAuthorizationMode.SshKey or StorageAuthorizationMode.UsernamePassword)
            || !_providerCatalog.GetRequired(providerId).AuthorizationModes.Contains(mode))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        if (stored is not null
            && mode == StorageAuthorizationMode.UsernamePassword
            && string.IsNullOrEmpty(request.Password)
            && !SameTarget(stored.SettingsJson, request.SettingsJson))
            throw new ArgumentException(R("Web_ExternalStorage_PasswordRequiredForChangedTarget"));

        var draft = new StorageConnection
        {
            ProviderId = providerId,
            AuthorizationMode = mode,
            SettingsJson = request.SettingsJson,
            EncryptedCredentialPayload = stored?.EncryptedCredentialPayload
        };
        if (stored is not null)
            draft.Id = stored.Id;
        if (DraftCredentials(request) is { } credentials)
            draft.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(draft, credentials);

        try
        {
            return (await _directoryTargets.ListDirectoriesAsync(draft, request.Path))
                .Select(item => new CloudDirectoryItem(item.Name, NormalizeRemotePath(item.Path)))
                .ToList();
        }
        catch (Exception exception)
        {
            // The picker shows a localized generic message; the cause stays diagnosable here.
            _logger.LogWarning(exception, "Remote folder browse failed for a {ProviderId} connection form", providerId);
            throw;
        }
    }

    private static Dictionary<string, string>? DraftCredentials(ConnectionFolderBrowseRequest request)
    {
        if (!string.IsNullOrEmpty(request.Password))
            return new(StringComparer.Ordinal)
            {
                ["username"] = request.Username?.Trim() ?? string.Empty,
                ["password"] = request.Password
            };
        return request.SshPrivateKey is { Length: > 0 } key
            ? new(StringComparer.Ordinal) { ["privateKeyBase64"] = Convert.ToBase64String(key) }
            : null;
    }

    /// <summary>Same server and identity: another folder on that server is not a new target.</summary>
    internal static bool SameTarget(string? stored, string submitted)
        => SameSettings(WithoutRemoteRoot(stored), WithoutRemoteRoot(submitted)!);

    private static string? WithoutRemoteRoot(string? settings)
    {
        try
        {
            if (JsonNode.Parse(settings ?? "null") is not JsonObject json)
                return settings;
            json.Remove("RemoteRoot");
            return json.ToJsonString();
        }
        catch (JsonException)
        {
            return settings;
        }
    }
}
