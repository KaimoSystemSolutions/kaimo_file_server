using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using static Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components.ConnectionDisplay;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

// In-place editing of an existing connection.
public partial class ConnectionAdministration
{
    // A connection whose provider is driven by interactive authorization
    // (OneDrive, Dropbox) exposes no configurable settings, so its editor is
    // limited to the display name and it is saved through UpdateConnectionAsync.
    private bool EditsNameOnly => SelectedProviderUsesInteractiveAuthorization;

    private async Task StartEditAsync()
    {
        if (_selected is null)
            return;
        _creating = false;
        _error = null;
        _testFailed = false;
        _activeConnectionTab = ConnectionTab.General;
        _editEnabled = _selected.State != StorageConnectionState.Disabled;
        _provider = _selected.ProviderId;
        _name = _selected.Name;
        _password = _username = _domain = _server = _serverUrl = string.Empty;
        _allowInsecureHttp = false;
        _remoteRoot = "/";
        _port = IsSmbProvider ? 445 : UsesPinnedSshSetup ? 22 : _port;
        _minimumDialect = "3.1.1";
        _sshHostFingerprint = _privateKeySecretReference = _knownHostsSecretReference = string.Empty;
        _sshUsePassword = _selected.AuthorizationMode == StorageAuthorizationMode.UsernamePassword;
        _settingsJson = _selected.SettingsJson ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(_selected.SettingsJson))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(_selected.SettingsJson);
                var root = document.RootElement;
                if (IsSmbProvider)
                {
                    _server = ReadJsonString(root, "Server") ?? string.Empty;
                    _port = ReadJsonInt(root, "Port") ?? 445;
                    _domain = ReadJsonString(root, "Domain") ?? string.Empty;
                    _minimumDialect = ReadJsonString(root, "MinimumDialect") ?? "3.1.1";
                }
                else if (IsWebDavProvider)
                {
                    _serverUrl = ReadJsonString(root, "ServerUrl") ?? string.Empty;
                    _allowInsecureHttp = root.TryGetProperty("AllowInsecureHttp", out var insecure)
                        && insecure.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                else if (UsesPinnedSshSetup)
                {
                    _server = ReadJsonString(root, "Host") ?? string.Empty;
                    _port = ReadJsonInt(root, "Port") ?? 22;
                    _username = ReadJsonString(root, "Username") ?? string.Empty;
                    _remoteRoot = ReadJsonString(root, "RemoteRoot") ?? "/";
                    _sshHostFingerprint = ReadJsonString(root, "ExpectedHostKeySha256") ?? string.Empty;
                    _privateKeySecretReference = ReadJsonString(root, "PrivateKeySecretReference") ?? string.Empty;
                    _knownHostsSecretReference = ReadJsonString(root, "KnownHostsSecretReference") ?? string.Empty;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }

        // Username and domain for username/password providers live in the
        // credential vault, not in the settings JSON. The password is never
        // returned; leaving the field blank keeps the stored one.
        if (IsSmbProvider || IsWebDavProvider)
        {
            var (username, domain) = await VM.GetConnectionCredentialFieldsAsync(_selected.Id);
            _username = username ?? string.Empty;
            _domain = domain ?? string.Empty;
        }

        _editing = true;
    }

    private void CancelEdit()
    {
        _editing = false;
        _password = string.Empty;
    }

    private async Task SaveEditAsync()
    {
        if (_selected is null)
            return;
        var connectionId = _selected.Id;
        bool usesUsernamePassword = IsSmbProvider || IsWebDavProvider || UsesSshPassword;
        string? settingsJson = EditsNameOnly ? null : BuildSettingsJson();
        await ActAsync(async () =>
        {
            if (EditsNameOnly)
                await VM.UpdateConnectionAsync(connectionId, _name);
            else
                await VM.UpdateConfiguredConnectionAsync(
                    connectionId, _name, settingsJson!,
                    usesUsernamePassword ? _username : null,
                    usesUsernamePassword && !string.IsNullOrEmpty(_password) ? _password : null,
                    IsSmbProvider ? _domain : null);
            // Reconcile the enabled toggle with the (re)computed state.
            var current = VM.Connections.FirstOrDefault(connection => connection.Id == connectionId);
            bool isDisabled = current?.State == StorageConnectionState.Disabled;
            if (_editEnabled && isDisabled)
                await VM.SetConnectionEnabledAsync(connectionId, true);
            else if (!_editEnabled && !isDisabled)
                await VM.SetConnectionEnabledAsync(connectionId, false);
        });
        if (_error is null)
        {
            _editing = false;
            _password = string.Empty;
        }
    }

    private bool CanSaveEdit => !string.IsNullOrWhiteSpace(_name) && !ConnectionNameTaken
        && (EditsNameOnly
            || (IsSmbProvider
                ? !string.IsNullOrWhiteSpace(_server) && _port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(_username)
                : IsWebDavProvider
                    ? !string.IsNullOrWhiteSpace(_serverUrl) && !string.IsNullOrWhiteSpace(_username)
                    : UsesPinnedSshSetup
                        ? !string.IsNullOrWhiteSpace(_server) && _port is >= 1 and <= 65535
                          && !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_remoteRoot)
                        : !string.IsNullOrWhiteSpace(_settingsJson)));

    private static string? ReadJsonString(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadJsonInt(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;
}
