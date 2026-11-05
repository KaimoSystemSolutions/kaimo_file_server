using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.Pages.CloudAccess.Components;

/// <summary>
/// "Provisioning" tab of a virtual share: offer it standalone (/files/access/{id}) or mount it
/// as a folder inside a local share. Validation lives in <see cref="CloudAccessMountService"/>.
/// </summary>
public partial class CloudAccessMountTab
{
    [Inject] private CloudAccessMountService Mounts { get; set; } = default!;
    [Inject] private IShareRepository Shares { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthState { get; set; } = default!;
    [Inject] private IUserContextFactory UserContexts { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;

    [Parameter, EditorRequired] public CloudAccessShare Share { get; set; } = default!;

    /// <summary>Raised after the provisioning changed, so the page can refresh its list.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private UserContext? _actor;
    private List<ShareDefinition> _hosts = [];
    private string? _mountedHostName;
    private Guid? _loadedFor;
    private CloudAccessProvisioning _mode;
    private Guid _hostId;
    private string _parentPath = "";
    private string _folderName = "";
    private bool _busy;
    private string? _error;

    private ShareDefinition? SelectedHost => _hosts.FirstOrDefault(host => host.Id == _hostId);

    private string? MountedLabel => Share.IsMounted && _mountedHostName is not null
        ? $"{_mountedHostName}/{Share.MountPath}"
        : null;

    private bool CanApply => !_busy && (_mode == CloudAccessProvisioning.VirtualShare
        ? Share.IsFolderMode
        : SelectedHost is not null && !string.IsNullOrWhiteSpace(_folderName) && !IsUnchanged);

    private bool IsUnchanged => Share.IsMounted && Share.MountShareId == _hostId
        && string.Equals(Share.MountPath, ShareRelativePath.Combine(_parentPath, _folderName.Trim()), StringComparison.Ordinal);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == Share.Id) return;
        _loadedFor = Share.Id;
        _error = null;
        _mode = Share.Provisioning;
        _hostId = Share.MountShareId ?? Guid.Empty;
        _parentPath = Share.IsMounted ? ShareRelativePath.GetParent(Share.MountPath) : "";
        _folderName = Share.IsMounted ? ShareRelativePath.GetFileName(Share.MountPath) : Share.Name;

        var state = await AuthState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        _actor = string.IsNullOrEmpty(username) ? null : await UserContexts.CreateByUsernameAsync(username);
        _hosts = _actor is null ? [] : await Mounts.GetHostSharesAsync(_actor);
        _mountedHostName = Share.MountShareId is { } hostId ? (await Shares.GetByIdAsync(hostId))?.Name : null;
    }

    private async Task ApplyAsync()
    {
        if (_actor is null) return;
        _busy = true;
        _error = null;
        try
        {
            var errorKey = _mode == CloudAccessProvisioning.VirtualShare
                ? await Mounts.UnmountAsync(_actor, Share)
                : await Mounts.MountAsync(_actor, Share, _hostId, _parentPath, _folderName);
            if (errorKey is not null)
            {
                _error = R(errorKey);
                return;
            }
            _mountedHostName = SelectedHost?.Name;
            Toasts.Show(R("Web_CloudAccess_MountSaved"), ToastType.Success);
            await OnChanged.InvokeAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
