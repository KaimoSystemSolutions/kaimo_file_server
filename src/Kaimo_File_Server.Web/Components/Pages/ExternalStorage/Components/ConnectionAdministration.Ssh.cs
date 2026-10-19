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

// Guided SSH setup (rsync-over-SSH, SFTP): key upload/generation, host-key discovery
// and the managed known_hosts entry. The private key is zeroed whenever it is replaced.
public partial class ConnectionAdministration
{
    private RsyncSshHostKeyCandidate? SelectedHostKey => _hostKeyCandidates.FirstOrDefault(
        candidate => candidate.FingerprintSha256 == _selectedHostFingerprint);

    private async Task UploadSshPrivateKeyAsync(InputFileChangeEventArgs args)
    {
        _sshSetupBusy = true; _error = null;
        try
        {
            await using Stream stream = args.File.OpenReadStream(1024 * 1024);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            byte[] uploaded = memory.ToArray();
            try { SetSshKey(await RsyncSshSetup.ValidatePrivateKeyAsync(uploaded), args.File.Name); }
            finally { CryptographicOperations.ZeroMemory(uploaded); }
        }
        catch (Exception exception) { _error = SetupError(exception); }
        finally { _sshSetupBusy = false; }
    }

    private async Task GenerateSshPrivateKeyAsync()
    {
        _sshSetupBusy = true; _error = null;
        try { SetSshKey(await RsyncSshSetup.GeneratePrivateKeyAsync(), R("Web_ExternalStorage_SshGeneratedKeyReady")); }
        catch (Exception exception) { _error = SetupError(exception); }
        finally { _sshSetupBusy = false; }
    }

    private void SetSshKey(RsyncSshKeyMaterial material, string name)
    {
        if (_sshPrivateKey is not null)
            CryptographicOperations.ZeroMemory(_sshPrivateKey);
        _sshPrivateKey = material.PrivateKey;
        _sshPublicKey = material.PublicKey;
        _sshPrivateKeyName = name;
    }

    private async Task CopyPublicKeyAsync()
    {
        if (_sshPublicKey.Length > 0)
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", _sshPublicKey);
    }

    private async Task DiscoverHostKeysAsync()
    {
        _sshSetupBusy = true; _error = null; _hostKeyConfirmed = false;
        try
        {
            _hostKeyCandidates = (await RsyncSshSetup.DiscoverHostKeysAsync(_server.Trim(), _port)).ToList();
            _selectedHostFingerprint = _hostKeyCandidates.FirstOrDefault()?.FingerprintSha256 ?? string.Empty;
            if (_hostKeyCandidates.Count == 0)
                _error = R("Web_ExternalStorage_SshHostKeyNotFound");
        }
        catch (Exception exception) { _error = SetupError(exception); }
        finally { _sshSetupBusy = false; }
    }

    private void HostKeySelectionChanged() => _hostKeyConfirmed = false;

    private async Task SshEndpointChangedAsync()
    {
        _hostKeyCandidates = [];
        _selectedHostFingerprint = string.Empty;
        _hostKeyConfirmed = false;
        if (_managedKnownHostsPath is not null)
        {
            await RsyncSshSetup.DeleteManagedKnownHostAsync(_managedKnownHostsPath);
            _managedKnownHostsPath = null;
        }
    }

    private async Task SshModeChangedAsync()
    {
        _error = null;
        if (_advancedSshSetup)
        {
            if (_sshPrivateKey is not null)
                CryptographicOperations.ZeroMemory(_sshPrivateKey);
            _sshPrivateKey = null;
            _sshPrivateKeyName = null;
            _sshPublicKey = string.Empty;
            await SshEndpointChangedAsync();
        }
        else
        {
            _sshHostFingerprint = string.Empty;
            _privateKeySecretReference = string.Empty;
            _knownHostsSecretReference = string.Empty;
        }
    }

    private async Task PrepareManagedSshSettingsAsync()
    {
        RsyncSshHostKeyCandidate candidate = SelectedHostKey
            ?? throw new InvalidOperationException(R("Web_ExternalStorage_SshHostKeyNotFound"));
        if (!_hostKeyConfirmed || _sshPrivateKey is null)
            throw new InvalidOperationException(R("Web_ExternalStorage_SshSetupIncomplete"));
        _managedKnownHostsPath ??= await RsyncSshSetup.PersistKnownHostAsync(candidate);
        _sshHostFingerprint = candidate.FingerprintSha256;
        _knownHostsSecretReference = _managedKnownHostsPath;
        _privateKeySecretReference = string.Empty;
    }

    private async Task ResetGuidedSshStateAsync()
    {
        if (_sshPrivateKey is not null)
            CryptographicOperations.ZeroMemory(_sshPrivateKey);
        _sshPrivateKey = null;
        _sshPrivateKeyName = null;
        _sshPublicKey = string.Empty;
        _hostKeyCandidates = [];
        _selectedHostFingerprint = string.Empty;
        _hostKeyConfirmed = false;
        if (_managedKnownHostsPath is not null)
            await RsyncSshSetup.DeleteManagedKnownHostAsync(_managedKnownHostsPath);
        _managedKnownHostsPath = null;
        _advancedSshSetup = false;
    }

    private static string SetupError(Exception exception)
        => exception is ProtocolConfigurationException configuration
            ? R($"Web_ExternalStorage_SshSetupError_{configuration.Code}")
            : R("Web_ExternalStorage_SshSetupError");
}
