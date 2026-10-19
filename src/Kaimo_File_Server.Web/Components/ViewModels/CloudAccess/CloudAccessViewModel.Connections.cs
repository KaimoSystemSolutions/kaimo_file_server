using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class CloudAccessViewModel
{
    public async Task<InteractiveAuthorization> CreateOneDriveConnectionAsync(string name)
    {
        await EnsureCanManageConnectionsAsync();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        await EnsureConnectionNameAvailableAsync(name.Trim());
        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderProfileId = WellKnownProviderProfiles.MicrosoftPublicClient,
            ProviderId = "onedrive",
            Name = name.Trim(),
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.PendingAuthorization
        };
        await _connections.SaveAsync(connection);
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "onedrive-access", _actor.User.Id);
        return new InteractiveAuthorization("onedrive", connection.Id, ticket);
    }

    /// <summary>
    /// Creates a pending Dropbox connection and returns the authorization page URL.
    /// Dropbox uses PKCE with only a public application key; no secret is stored.
    /// </summary>
    public async Task<InteractiveAuthorization> CreateDropboxConnectionAsync(string name)
    {
        await EnsureCanManageConnectionsAsync();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        await EnsureConnectionNameAvailableAsync(name.Trim());
        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderId = "dropbox",
            Name = name.Trim(),
            AuthorizationMode = StorageAuthorizationMode.DelegatedAuthorizationCode,
            State = StorageConnectionState.PendingAuthorization
        };
        await _connections.SaveAsync(connection);
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, "dropbox-access", _actor.User.Id);
        return new InteractiveAuthorization("dropbox", connection.Id, ticket);
    }

    /// <summary>
    /// Creates a provider-configured protocol connection. Passwords are stored
    /// only in the context-bound credential vault and never in SettingsJson.
    /// </summary>
    public async Task CreateConfiguredConnectionAsync(
        string providerId,
        string name,
        string settingsJson,
        string? username = null,
        string? password = null,
        string? domain = null,
        byte[]? sshPrivateKey = null)
    {
        await EnsureCanManageConnectionsAsync();
        if (_providerCatalog is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var provider = _providerCatalog.GetRequired(providerId);
        if (provider.AuthorizationModes.Count != 1)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var authorizationMode = provider.AuthorizationModes.Single();
        if (authorizationMode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.UsernamePassword
            or StorageAuthorizationMode.NetworkIdentity))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        string normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        await EnsureConnectionNameAvailableAsync(normalizedName);
        if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.Length > 64 * 1024)
            throw new ArgumentException(R("Web_ExternalStorage_InvalidSettings"));
        bool hasUsername = !string.IsNullOrWhiteSpace(username);
        bool hasPassword = !string.IsNullOrEmpty(password);
        if (string.Equals(provider.Id, "smb", StringComparison.OrdinalIgnoreCase)
            && (!hasUsername || !hasPassword))
            throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
        if (authorizationMode == StorageAuthorizationMode.SshKey
            && sshPrivateKey is { Length: > 1024 * 1024 })
            throw new ArgumentException(R("Web_ExternalStorage_InvalidPrivateKey"));

        var connection = new StorageConnection
        {
            CreatedByUserId = _actor!.User.Id,
            ProviderId = provider.Id,
            Name = normalizedName,
            AuthorizationMode = authorizationMode,
            SettingsJson = settingsJson,
            State = StorageConnectionState.PendingConfiguration
        };
        if (hasUsername || hasPassword)
        {
            if (!hasUsername || !hasPassword || _credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = username!.Trim(),
                ["password"] = password!
            };
            if (!string.IsNullOrWhiteSpace(domain))
                credentials["domain"] = domain.Trim();
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(connection, credentials);
            connection.AccountDisplayName = username.Trim();
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }
        else if (sshPrivateKey is { Length: > 0 })
        {
            if (_credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidPrivateKey"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["privateKeyBase64"] = Convert.ToBase64String(sshPrivateKey)
            };
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(
                connection, credentials);
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }
        var health = await provider.TestAsync(connection);
        connection.State = MapUnhealthyState(connection, health);
        connection.LastVerifiedAtUtc = health.CheckedAtUtc;
        connection.LastErrorCode = health.IsHealthy ? null : health.Code;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    public async Task<InteractiveAuthorization> AuthorizeConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var provider = _providerCatalog?.GetRequired(connection.ProviderId);
        if (provider is not null
            && !provider.Capabilities.HasFlag(StorageProviderCapabilities.DelegatedAuthorization))
            throw new NotSupportedException(R("Web_ExternalStorage_AuthorizeUnsupported"));

        // Each interactive provider owns its own authorization page and ticket
        // purpose. Device code (OneDrive) and PKCE code paste (Dropbox) differ.
        var (endpoint, purpose) = connection.AuthorizationMode switch
        {
            StorageAuthorizationMode.DeviceCode => ("onedrive", "onedrive-access"),
            StorageAuthorizationMode.DelegatedAuthorizationCode when connection.ProviderId == "dropbox"
                => ("dropbox", "dropbox-access"),
            _ => throw new NotSupportedException(R("Web_ExternalStorage_AuthorizeUnsupported"))
        };
        var ticket = await _tickets.IssueAsync(
            connection.Id, string.Empty, purpose, _actor!.User.Id);
        return new InteractiveAuthorization(endpoint, connection.Id, ticket);
    }

    /// <summary>Updates the display name of a managed provider connection.</summary>
    public async Task UpdateConnectionAsync(Guid connectionId, string name)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        await EnsureConnectionNameAvailableAsync(normalizedName, connection);

        connection.Name = normalizedName;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>
    /// Updates a configured protocol connection in place: display name, connection
    /// settings, and — for username/password providers — the stored credentials.
    /// Leaving the password empty preserves the existing one so the operator can
    /// adjust the username or domain without re-entering it — but only while the
    /// connection settings (host, URL, share, …) are unchanged: otherwise the stored
    /// password would be sent to a target it was never meant for. The provider,
    /// department, and any SSH key material stay unchanged.
    /// </summary>
    public async Task UpdateConfiguredConnectionAsync(
        Guid connectionId,
        string name,
        string settingsJson,
        string? username = null,
        string? password = null,
        string? domain = null)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_providerCatalog is null)
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var provider = _providerCatalog.GetRequired(connection.ProviderId);
        if (connection.AuthorizationMode is not (StorageAuthorizationMode.HostMount
            or StorageAuthorizationMode.SshKey
            or StorageAuthorizationMode.UsernamePassword
            or StorageAuthorizationMode.NetworkIdentity))
            throw new NotSupportedException(R("Web_ExternalStorage_ProviderUnsupported"));
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 200)
            throw new ArgumentException(R("Web_CloudAccess_InvalidConnectionName"));
        await EnsureConnectionNameAvailableAsync(normalizedName, connection);
        if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.Length > 64 * 1024)
            throw new ArgumentException(R("Web_ExternalStorage_InvalidSettings"));

        if (connection.AuthorizationMode == StorageAuthorizationMode.UsernamePassword
            && string.IsNullOrEmpty(password)
            && !SameSettings(connection.SettingsJson, settingsJson))
            throw new ArgumentException(R("Web_ExternalStorage_PasswordRequiredForChangedTarget"));

        connection.Name = normalizedName;
        connection.SettingsJson = settingsJson;

        if (connection.AuthorizationMode == StorageAuthorizationMode.UsernamePassword)
        {
            if (_credentialVault is null)
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var existing = string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : _credentialVault.UnprotectConnectionCredentials(connection);
            var resolvedUsername = string.IsNullOrWhiteSpace(username)
                ? existing.GetValueOrDefault("username") ?? string.Empty
                : username.Trim();
            var resolvedPassword = string.IsNullOrEmpty(password)
                ? existing.GetValueOrDefault("password") ?? string.Empty
                : password;
            if (string.IsNullOrWhiteSpace(resolvedUsername) || string.IsNullOrEmpty(resolvedPassword))
                throw new ArgumentException(R("Web_ExternalStorage_InvalidCredentials"));
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = resolvedUsername,
                ["password"] = resolvedPassword
            };
            if (!string.IsNullOrWhiteSpace(domain))
                credentials["domain"] = domain.Trim();
            connection.EncryptedCredentialPayload = _credentialVault.ProtectConnectionCredentials(connection, credentials);
            connection.AccountDisplayName = resolvedUsername;
            connection.CredentialUpdatedAtUtc = DateTime.UtcNow;
        }

        var health = await provider.TestAsync(connection);
        connection.State = MapUnhealthyState(connection, health);
        connection.LastVerifiedAtUtc = health.CheckedAtUtc;
        connection.LastErrorCode = health.IsHealthy ? null : health.Code;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>Compares settings semantically, so reformatted but equal JSON is not a change.</summary>
    internal static bool SameSettings(string? stored, string submitted)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(stored ?? "null"),
                System.Text.Json.Nodes.JsonNode.Parse(submitted));
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Equals(stored, submitted, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Returns the stored username and domain for prefilling the connection edit
    /// form. The password is never returned; leaving it blank on save keeps it.
    /// </summary>
    public async Task<(string? Username, string? Domain)> GetConnectionCredentialFieldsAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_credentialVault is null || string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload))
            return (connection.AccountDisplayName, null);
        try
        {
            var credentials = _credentialVault.UnprotectConnectionCredentials(connection);
            return (credentials.GetValueOrDefault("username") ?? connection.AccountDisplayName,
                    credentials.GetValueOrDefault("domain"));
        }
        catch
        {
            return (connection.AccountDisplayName, null);
        }
    }

    /// <summary>Disables or re-enables a connection without removing its consumers.</summary>
    public async Task SetConnectionEnabledAsync(Guid connectionId, bool enabled)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        connection.State = enabled
            ? RequiresProtectedCredential(connection.AuthorizationMode)
              && string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
                ? StorageConnectionState.PendingAuthorization
                : StorageConnectionState.Ready
            : StorageConnectionState.Disabled;
        connection.LastErrorCode = null;
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    /// <summary>
    /// Verifies a connection through its provider-neutral health contract.
    /// Provider response details remain behind the sanitized provider boundary.
    /// </summary>
    public async Task TestConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        if (_providerCatalog is null)
        {
            if (!string.Equals(connection.ProviderId, "onedrive", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(R("Web_ExternalStorage_TestUnsupported"));
            await ListRemoteFoldersAsync(connectionId, "/");
            connection = await GetManagedConnectionAsync(connectionId);
            connection.State = StorageConnectionState.Ready;
            connection.LastVerifiedAtUtc = DateTime.UtcNow;
            connection.LastErrorCode = null;
        }
        else
        {
            var result = await _providerCatalog.GetRequired(connection.ProviderId).TestAsync(connection);
            connection.State = MapUnhealthyState(connection, result);
            connection.LastVerifiedAtUtc = result.CheckedAtUtc;
            connection.LastErrorCode = result.IsHealthy ? null : result.Code;
        }
        await _connections.SaveAsync(connection);
        await LoadAsync();
    }

    public async Task<List<CloudDirectoryItem>> ListRemoteFoldersAsync(Guid connectionId, string path)
    {
        var connectionRecord = await GetUsableConnectionAsync(connectionId);
        if (connectionRecord.State != StorageConnectionState.Ready)
            throw new InvalidOperationException(R("Web_CloudAccess_ConnectionNotReady"));
        if (_directoryTargets is not null)
            return (await _directoryTargets.ListDirectoriesAsync(connectionRecord, path))
                .Select(item => new CloudDirectoryItem(item.Name, NormalizeRemotePath(item.Path)))
                .ToList();

        // Compatibility path for isolated legacy hosts without the provider catalog.
        if (!ShareRelativePath.TryNormalizeStrict(path.Trim('/'), out var normalized))
            throw new UnauthorizedAccessException(R("Web_CloudAccess_InvalidRemotePath"));
        await using var connection = _oneDriveConnections.Create(connectionRecord);
        var items = await connection.ListDetailedAsync(normalized);
        return items.Where(x => x.IsDirectory)
            .Select(x => new CloudDirectoryItem(x.Name, "/" + x.Path.Trim('/')))
            .OrderBy(x => x.Name).ToList();
    }

    public async Task DeleteConnectionAsync(Guid connectionId)
    {
        var connection = await GetManagedConnectionAsync(connectionId);
        var result = await _connections.DeleteAsync(connection.Id);
        if (result == StorageConnectionDeleteResult.InUse)
            throw new InvalidOperationException(R("Web_StorageConnection_DeleteInUse"));
        if (result == StorageConnectionDeleteResult.Deleted
            && _providerCatalog?.TryGet(connection.ProviderId, out var provider) == true)
        {
            // The record is already gone: provider cleanup (cached clients, managed files) is
            // best-effort and must not turn a successful delete into an error or skip the reload.
            try { await provider.RevokeAsync(connection); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Cleanup after deleting storage connection {ConnectionId} failed", connection.Id);
            }
        }
        await LoadAsync();
    }
}
