using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;

// Remote root picker of the SSH connection form; it works before the connection is saved.
public partial class ConnectionAdministration
{
    private bool _rootPickerOpen;
    private bool _rootPickerLoading;
    private bool _rootPickerNotSyncable;
    private string _rootPickerPath = "/";
    private string? _rootPickerError;
    private IReadOnlyList<CloudDirectoryItem> _rootPickerItems = [];

    // Everything a connection attempt needs. Saved connections keep their key or password.
    private bool SshReadyToConnect => !string.IsNullOrWhiteSpace(_server)
        && _port is >= 1 and <= 65535
        && !string.IsNullOrWhiteSpace(_username)
        && (_editing
            ? !string.IsNullOrWhiteSpace(_sshHostFingerprint)
            : (!UsesSshPassword || !string.IsNullOrEmpty(_password))
              && (_advancedSshSetup
                  ? !string.IsNullOrWhiteSpace(_sshHostFingerprint)
                    && (UsesSshPassword || !string.IsNullOrWhiteSpace(_privateKeySecretReference))
                    && (IsSftpProvider || !string.IsNullOrWhiteSpace(_knownHostsSecretReference))
                  : (UsesSshPassword || _sshPrivateKey is not null)
                    && SelectedHostKey is not null
                    && _hostKeyConfirmed));

    private async Task OpenRootPickerAsync()
    {
        _error = null;
        try
        {
            if (!_editing && !_advancedSshSetup)
                await PrepareManagedSshSettingsAsync();
        }
        catch (Exception exception)
        {
            _error = SetupError(exception);
            return;
        }
        _rootPickerOpen = true;
        await LoadRootPickerAsync(string.IsNullOrWhiteSpace(_remoteRoot) ? "/" : _remoteRoot.Trim());
    }

    // Browses from the server root, so what gets picked is the absolute remote root.
    private async Task LoadRootPickerAsync(string path)
    {
        _rootPickerPath = path;
        _rootPickerLoading = true;
        _rootPickerError = null;
        _rootPickerNotSyncable = false;
        try
        {
            _rootPickerItems = await VM.ListDraftConnectionFoldersAsync(new ConnectionFolderBrowseRequest(
                _editing ? _selected?.Id : null,
                _provider,
                UsesSshPassword ? StorageAuthorizationMode.UsernamePassword : StorageAuthorizationMode.SshKey,
                BuildSettingsJson(remoteRoot: "/"),
                path,
                UsesSshPassword ? _username : null,
                UsesSshPassword ? _password : null,
                UsesSshPassword ? null : _sshPrivateKey));
        }
        catch (RemoteFolderNotSyncableException notSyncable)
        {
            _rootPickerItems = notSyncable.Folders.Select(folder => new CloudDirectoryItem(folder.Name, folder.Path)).ToArray();
            _rootPickerNotSyncable = true;
            _rootPickerError = ConnectionDisplay.ErrorMessage(notSyncable.Code);
        }
        catch (ArgumentException exception)
        {
            _rootPickerItems = [];
            _rootPickerError = exception.Message;
        }
        catch
        {
            _rootPickerItems = [];
            _rootPickerError = R("Web_ExternalStorage_RemoteBrowseFailed");
        }
        finally { _rootPickerLoading = false; }
    }

    private void SelectRootPickerFolder(string path)
    {
        _remoteRoot = path;
        CloseRootPicker();
    }

    private void CloseRootPicker()
    {
        _rootPickerOpen = false;
        _rootPickerItems = [];
        _rootPickerError = null;
    }
}
