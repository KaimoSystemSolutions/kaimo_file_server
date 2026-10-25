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

// Connection list, create flow, test/authorize/delete actions. Editing lives in
// ConnectionAdministration.Edit.cs, the guided SSH key setup in ConnectionAdministration.Ssh.cs.
public partial class ConnectionAdministration
{
    /// <summary>Opens the syncs tab; the syncs tile shows no link without it.</summary>
    [Parameter] public EventCallback OnOpenSyncs { get; set; }

    private StorageConnection? _selected;
    private bool _creating;
    private bool _busy;
    private string _provider = "onedrive";
    private string _name = string.Empty;
    private string _settingsJson = string.Empty;
    private string _server = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _domain = string.Empty;
    private int _port = 445;
    private string _minimumDialect = "3.1.1";
    private string _serverUrl = string.Empty;
    private bool _allowInsecureHttp;
    private string _remoteRoot = "/";
    private string _sshHostFingerprint = string.Empty;
    private string _privateKeySecretReference = string.Empty;
    private string _knownHostsSecretReference = string.Empty;
    private bool _advancedSshSetup;
    private bool _sshUsePassword;
    private bool _sshSetupBusy;
    private byte[]? _sshPrivateKey;
    private string? _sshPrivateKeyName;
    private string _sshPublicKey = string.Empty;
    private List<RsyncSshHostKeyCandidate> _hostKeyCandidates = [];
    private string _selectedHostFingerprint = string.Empty;
    private bool _hostKeyConfirmed;
    private string? _managedKnownHostsPath;
    private bool _testFailed;
    private bool _confirmDelete;
    private string? _error;
    private bool _editing;
    private bool _editEnabled = true;
    private enum ConnectionTab { General, Connection, Security }
    private ConnectionTab _activeConnectionTab = ConnectionTab.General;
    private bool ShowConnectionTab => !SelectedProviderUsesInteractiveAuthorization;
    private bool ShowSecurityTab => IsSmbProvider || IsWebDavProvider || UsesPinnedSshSetup;
    private IEnumerable<IStorageConnectionProvider> CreatableProviders => ProviderCatalog.Providers
        .Where(provider => provider.AuthorizationModes.Any(mode => mode is StorageAuthorizationMode.DeviceCode
            or StorageAuthorizationMode.UsernamePassword or StorageAuthorizationMode.NetworkIdentity
            or StorageAuthorizationMode.HostMount or StorageAuthorizationMode.SshKey)
            || IsZeroConfigDelegated(provider))
        .OrderBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    private IStorageConnectionProvider? SelectedProvider => ProviderCatalog.TryGet(_provider, out var provider) ? provider : null;
    // A provider whose only authorization mode is delegated authorization code is
    // driven interactively with just a public app key (Dropbox). Providers that
    // also require operator-supplied secrets (Google) declare additional modes.
    private static bool IsZeroConfigDelegated(IStorageConnectionProvider provider)
        => provider.AuthorizationModes.Count == 1
           && provider.AuthorizationModes.Contains(StorageAuthorizationMode.DelegatedAuthorizationCode);
    private bool SelectedProviderUsesInteractiveAuthorization =>
        SelectedProvider?.AuthorizationModes.Contains(StorageAuthorizationMode.DeviceCode) == true
        || (SelectedProvider is not null && IsZeroConfigDelegated(SelectedProvider));
    private bool IsSmbProvider => string.Equals(_provider, "smb", StringComparison.OrdinalIgnoreCase);
    private bool IsRsyncSshProvider => string.Equals(_provider, "rsync-ssh", StringComparison.OrdinalIgnoreCase);
    private bool IsSftpProvider => string.Equals(_provider, "sftp", StringComparison.OrdinalIgnoreCase);
    private bool IsWebDavProvider => string.Equals(_provider, "webdav", StringComparison.OrdinalIgnoreCase);
    private bool IsInsecureWebDavUrl => _serverUrl.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    // rsync-over-SSH and SFTP share the identical pinned-SSH setup (key + host key).
    private bool UsesPinnedSshSetup => IsRsyncSshProvider || IsSftpProvider;
    // SFTP may sign in with a password instead of a key; rsync stays key-only.
    private bool UsesSshPassword => IsSftpProvider && _sshUsePassword;
    /// <summary>Live duplicate-name check against the loaded list; the view model re-checks on save.</summary>
    private bool ConnectionNameTaken => !string.IsNullOrWhiteSpace(_name)
        && !(_editing && string.Equals(_selected?.Name, _name.Trim(), StringComparison.Ordinal))
        && VM.Connections.Any(connection => (!_editing || connection.Id != _selected?.Id)
            && string.Equals(connection.Name, _name.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool CanCreate => !string.IsNullOrWhiteSpace(_name) && !ConnectionNameTaken
                              && SelectedProvider is not null
                              && (SelectedProviderUsesInteractiveAuthorization
                                  || (IsSmbProvider && !string.IsNullOrWhiteSpace(_server)
                                      && _port is >= 1 and <= 65535
                                      && !string.IsNullOrWhiteSpace(_username)
                                      && !string.IsNullOrEmpty(_password))
                                  || (IsWebDavProvider && !string.IsNullOrWhiteSpace(_serverUrl)
                                      && !string.IsNullOrWhiteSpace(_username)
                                      && !string.IsNullOrEmpty(_password))
                                  || (UsesPinnedSshSetup && !string.IsNullOrWhiteSpace(_server)
                                      && _port is >= 1 and <= 65535
                                      && !string.IsNullOrWhiteSpace(_username)
                                      && !string.IsNullOrWhiteSpace(_remoteRoot)
                                      && (!UsesSshPassword || !string.IsNullOrEmpty(_password))
                                      && (_advancedSshSetup
                                          ? !string.IsNullOrWhiteSpace(_sshHostFingerprint)
                                            && (UsesSshPassword || !string.IsNullOrWhiteSpace(_privateKeySecretReference))
                                            && (IsSftpProvider || !string.IsNullOrWhiteSpace(_knownHostsSecretReference))
                                          : (UsesSshPassword || _sshPrivateKey is not null)
                                            && SelectedHostKey is not null
                                            && _hostKeyConfirmed))
                                  || (!IsSmbProvider && !UsesPinnedSshSetup
                                      && !string.IsNullOrWhiteSpace(_settingsJson)));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await VM.LoadAsync();
        StateHasChanged();
    }

    private async Task CreateAsync() => await ActAsync(async () =>
    {
        if (SelectedProviderUsesInteractiveAuthorization)
        {
            var authorization = SelectedProvider!.AuthorizationModes.Contains(StorageAuthorizationMode.DeviceCode)
                ? await VM.CreateOneDriveConnectionAsync(_name)
                : await VM.CreateDropboxConnectionAsync(_name);
            await StartAuthorizationAsync(authorization);
        }
        else
        {
            if (UsesPinnedSshSetup && !_advancedSshSetup)
                await PrepareManagedSshSettingsAsync();
            string settingsJson = BuildSettingsJson();
            bool usesUsernamePassword = IsSmbProvider || IsWebDavProvider || UsesSshPassword;
            await VM.CreateConfiguredConnectionAsync(
                _provider, _name, settingsJson,
                usesUsernamePassword ? _username : null,
                usesUsernamePassword ? _password : null,
                IsSmbProvider ? _domain : null,
                UsesPinnedSshSetup && !_advancedSshSetup && !UsesSshPassword ? _sshPrivateKey : null,
                UsesPinnedSshSetup
                    ? UsesSshPassword ? StorageAuthorizationMode.UsernamePassword : StorageAuthorizationMode.SshKey
                    : null);
            if (_sshPrivateKey is not null)
                CryptographicOperations.ZeroMemory(_sshPrivateKey);
            _sshPrivateKey = null;
            _managedKnownHostsPath = null;
            _creating = false;
            _selected = VM.Connections.FirstOrDefault(connection => connection.Name == _name);
        }
    });
    private async Task AuthorizeAsync() => await ActAsync(async () => await StartAuthorizationAsync(await VM.AuthorizeConnectionAsync(_selected!.Id)));

    // POSTs the single-use ticket in a form body (keeps the secret out of the URL).
    private async Task StartAuthorizationAsync(InteractiveAuthorization authorization)
        => await JS.InvokeVoidAsync(
            "kaimoPostConnect",
            $"/api/cloud-access/{authorization.Endpoint}/connect",
            authorization.ConnectionId,
            authorization.Ticket);
    private async Task TestAsync()
    {
        _testFailed = false;
        Guid selectedId = _selected!.Id;
        _busy = true; _error = null;
        try
        {
            await VM.TestConnectionAsync(selectedId);
            _selected = VM.Connections.FirstOrDefault(connection => connection.Id == selectedId);
            // Success is transient (toast); a failure stays as a banner until the next test.
            _testFailed = !(_selected?.State == StorageConnectionState.Ready
                            && string.IsNullOrWhiteSpace(_selected.LastErrorCode));
            if (!_testFailed)
                Toasts.Show(R("Web_ExternalStorage_TestSuccessful"), ToastType.Success);
        }
        catch (Exception exception)
        {
            _error = exception is InvalidOperationException or ArgumentException
                ? exception.Message
                : R("Web_ExternalStorage_OperationFailed");
            _testFailed = true;
        }
        finally { _busy = false; }
    }
    private async Task DeleteAsync()
    {
        _confirmDelete = false;
        await ActAsync(() => VM.DeleteConnectionAsync(_selected!.Id));
    }
    private async Task ProviderChanged()
    {
        if (!UsesPinnedSshSetup)
            await ResetGuidedSshStateAsync();
        if (!IsSftpProvider)
            _sshUsePassword = false;
        _port = IsSmbProvider ? 445 : UsesPinnedSshSetup ? 22 : _port;
        _remoteRoot = "/";
        _serverUrl = string.Empty;
        _allowInsecureHttp = false;
        // The available tabs depend on the provider; keep the selection valid.
        _activeConnectionTab = ConnectionTab.General;
    }
    private async Task ActAsync(Func<Task> action)
    {
        Guid? selectedId = _selected?.Id;
        _busy = true; _error = null;
        try { await action(); _selected = selectedId is Guid id ? VM.Connections.FirstOrDefault(c => c.Id == id) : VM.Connections.FirstOrDefault(); }
        catch (InvalidOperationException exception) { _error = exception.Message; }
        catch (ArgumentException exception) { _error = exception.Message; }
        catch { _error = R("Web_ExternalStorage_OperationFailed"); }
        finally { _busy = false; }
    }
    private string BuildSettingsJson()
    {
        if (IsWebDavProvider)
            // The opt-in is only written when set, so existing HTTPS settings stay byte-identical.
            return _allowInsecureHttp && IsInsecureWebDavUrl
                ? System.Text.Json.JsonSerializer.Serialize(new { ServerUrl = _serverUrl.Trim(), AllowInsecureHttp = true })
                : System.Text.Json.JsonSerializer.Serialize(new { ServerUrl = _serverUrl.Trim() });
        if (IsSmbProvider)
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                Server = _server.Trim(),
                Port = _port,
                Domain = string.IsNullOrWhiteSpace(_domain) ? null : _domain.Trim(),
                MinimumDialect = _minimumDialect,
                RequireSigning = true,
                RequireEncryption = true
            });
        if (UsesPinnedSshSetup)
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                Host = _server.Trim(),
                Port = _port,
                Username = _username.Trim(),
                RemoteRoot = _remoteRoot.Trim(),
                ExpectedHostKeySha256 = _sshHostFingerprint.Trim(),
                PrivateKeySecretReference = UsesSshPassword ? string.Empty : _privateKeySecretReference.Trim(),
                KnownHostsSecretReference = _knownHostsSecretReference.Trim()
            });
        return _settingsJson;
    }
    private async Task CancelCreateAsync()
    {
        await ResetGuidedSshStateAsync();
        _creating = false;
        _name = _settingsJson = _server = _username = _password = _domain = _serverUrl = string.Empty;
        _allowInsecureHttp = false;
        _sshUsePassword = false;
        _sshHostFingerprint = _privateKeySecretReference = _knownHostsSecretReference = string.Empty;
        _remoteRoot = "/";
        _port = 445;
        _minimumDialect = "3.1.1";
    }
    private StorageConnectionUsage Usage(Guid id) => VM.ConnectionUsage.GetValueOrDefault(id) ?? new StorageConnectionUsage(0, 0);
    private string ProviderName(string id) => ProviderCatalog.TryGet(id, out var provider) ? provider.DisplayName : id;
    private bool Supports(StorageConnection connection, StorageProviderCapabilities capability)
        => ProviderCatalog.TryGet(connection.ProviderId, out var provider) && provider.Capabilities.HasFlag(capability);
    private bool SupportsAuthorization(StorageConnection connection)
        => Supports(connection, StorageProviderCapabilities.DelegatedAuthorization)
           && (connection.AuthorizationMode == StorageAuthorizationMode.DeviceCode
               || (connection.AuthorizationMode == StorageAuthorizationMode.DelegatedAuthorizationCode
                   && ProviderCatalog.TryGet(connection.ProviderId, out var provider)
                   && IsZeroConfigDelegated(provider)));
    private string CapabilityNames(StorageConnection connection)
    {
        if (!ProviderCatalog.TryGet(connection.ProviderId, out var provider))
            return R("Web_ExternalStorage_None");
        var names = Enum.GetValues<StorageProviderCapabilities>()
            .Where(capability => capability != StorageProviderCapabilities.None
                                 && provider.Capabilities.HasFlag(capability))
            .Select(capability => R($"Web_ExternalStorage_Capability_{capability}"));
        return string.Join(", ", names);
    }
    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private void SelectConnection(StorageConnection connection)
    {
        _selected = connection;
        _testFailed = false;
        _confirmDelete = false;
        _editing = false;
    }

    private async Task ToggleCreateAsync()
    {
        bool opening = !_creating;
        await CancelCreateAsync();
        if (opening)
        {
            _editing = false;
            _provider = "onedrive";
            _activeConnectionTab = ConnectionTab.General;
        }
        _creating = opening;
    }

    public async ValueTask DisposeAsync() => await ResetGuidedSshStateAsync();
}
