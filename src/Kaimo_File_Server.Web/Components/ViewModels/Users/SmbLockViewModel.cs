using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// User details → SMB lockout (AP8e). Samba locks an account itself after too many failed SMB
/// logins; the bridge records that lock in the security overview. This shows the running lock
/// and lets an administrator with <see cref="ManagementPermission.EnableDisableUsers"/> on the
/// user request an unlock, which the Samba user sync applies with its next cycle. Like the other
/// sign-in switches it is not offered for one's own account; the permission is re-checked on
/// the click.
/// </summary>
public sealed class SmbLockViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    IUserRepository users,
    ISecurityEventRepository securityEvents,
    ILogger<SmbLockViewModel> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private User? _user;

    public bool CanUnlock { get; private set; }

    /// <summary>End of the running SMB lock, when the bridge has recorded one.</summary>
    public DateTime? LockedUntilUtc { get; private set; }

    /// <summary>An unlock was requested in this view; it takes effect with the next Samba user sync.</summary>
    public bool UnlockRequested { get; private set; }

    public bool IsBusy { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync(User user)
    {
        _user = user;
        UnlockRequested = false;
        ErrorMessage = null;
        LockedUntilUtc = null;
        try
        {
            CanUnlock = await MayUnlockAsync(user);
            // A lock that began before the last unlock request has been lifted already.
            LockedUntilUtc = await securityEvents.GetActiveLockUntilAsync(SecurityChannel.Smb, user.Username,
                user.SmbUnlockRequestedAtUtc ?? DateTime.MinValue, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the SMB lockout state of user {UserId}", user.Id);
        }
    }

    public async Task UnlockAsync()
    {
        ErrorMessage = null;
        if (_user is not { } user) return;
        if (!await MayUnlockAsync(user))
        {
            CanUnlock = false;
            ErrorMessage = R("Web_Settings_NoPermissionChange");
            return;
        }

        IsBusy = true;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            await users.RequestSmbUnlockAsync(user.Id, now);
            // The overview stops counting the lock; Samba's next lock is recorded as a new one.
            await securityEvents.EndActiveLocksAsync(SecurityChannel.Smb, user.Username, now);
            user.SmbUnlockRequestedAtUtc = now;
            LockedUntilUtc = null;
            UnlockRequested = true;
            logger.LogInformation("SMB unlock requested for user {UserId}", user.Id);
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to request an SMB unlock for user {UserId}", user.Id);
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> MayUnlockAsync(User user)
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        var actor = string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
        return actor is not null && actor.User.Id != user.Id
               && await mgmtAuth.CanManageUserAsync(actor, user.Id, ManagementPermission.EnableDisableUsers);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
