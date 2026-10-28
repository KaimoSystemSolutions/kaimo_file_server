using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// User edit → "Services" tab: which file services (client app, web login, SMB, WebDAV) the
/// selected user may use. Like the sign-in toggles, they are locked on one's own account and
/// need <c>EnableDisableUsers</c> (see <c>SaveUserAsync</c>), and a member of the Admins group
/// always keeps the web login (see <c>ServiceAccessGate</c>).
/// </summary>
public partial class UserListViewModel
{
    /// <summary>Working copy of the selected user's <see cref="User.ServiceAccess"/>.</summary>
    public UserServiceAccess EditUserServiceAccess { get; set; } = UserServiceAccess.All;

    private UserServiceAccess _servicesOffGlobally;

    /// <summary>The selected user belongs to the Admins group, so the web login cannot be switched off.</summary>
    public bool SelectedUserKeepsWebLogin => UserGroups.Any(g => g.Id == WellKnownGUIDs.GROUP_ADMINS);

    public bool EditUserHasService(UserServiceAccess service)
        => EditUserServiceAccess.HasFlag(service)
           || (service == UserServiceAccess.Web && SelectedUserKeepsWebLogin);

    public void SetEditUserService(UserServiceAccess service, bool allowed)
        => EditUserServiceAccess = allowed ? EditUserServiceAccess | service : EditUserServiceAccess & ~service;

    /// <summary>The service is switched off for everyone on the settings page.</summary>
    public bool IsServiceOffGlobally(UserServiceAccess service) => _servicesOffGlobally.HasFlag(service);

    private async Task LoadEditServicesAsync(User user)
    {
        EditUserServiceAccess = user.ServiceAccess;
        _servicesOffGlobally = UserServiceAccess.None;
        if (_serviceAccess is null) return;
        foreach (var service in FileServiceAccessViewModel.Services)
            if (!await _serviceAccess.IsGloballyEnabledAsync(service))
                _servicesOffGlobally |= service;
    }

    private async Task SaveEditServicesAsync(User user)
    {
        // Own account: locked like the other sign-in toggles.
        if (IsViewingSelf) return;

        var access = EditUserServiceAccess;
        // An administrator's stored web flag stays as it is; the gate exempts them anyway.
        if (SelectedUserKeepsWebLogin)
            access = (access & ~UserServiceAccess.Web) | (user.ServiceAccess & UserServiceAccess.Web);
        if (access != user.ServiceAccess)
            await _userRepo.UpdateServiceAccessAsync(user.Id, access);
    }
}
