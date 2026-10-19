using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class UserListViewModel
{
    // -- Profile picture edit (shared by admin full-edit and self-service) --
    /// <summary>Upload cap. Comfortably above the AD <c>thumbnailPhoto</c> ~100 KB norm while
    /// keeping the base64 payload embedded on the page small.</summary>
    public const int MaxPhotoBytes = 256 * 1024;
    public byte[]? EditUserPhoto { get; private set; }
    public string? EditUserPhotoContentType { get; private set; }
    private bool _photoChanged;
    public bool HasEditPhoto => EditUserPhoto is { Length: > 0 };
    public string? EditPhotoDataUri => DynamicHelpers.UserAvatar.DataUri(EditUserPhoto, EditUserPhotoContentType);

    /// <summary>
    /// Self-service save: persists only the Tier-1 personal fields (given/sur name, photo)
    /// and — when the account permits and the current password checks out — the user's own
    /// password. Never touches identity, security or membership fields.
    /// </summary>
    private async Task SaveSelfProfileAsync()
    {
        if (SelectedUser is null) return;

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            if (!string.IsNullOrWhiteSpace(NewPassword))
            {
                if (!SelectedUser.CanChangePassword)
                { ErrorMessage = Resources.Web_User_NoPermissionPasswordReset; return; }
                var pwError = (await GetPasswordPolicyAsync()).Validate(NewPassword);
                if (pwError is not null) { ErrorMessage = pwError; return; }
                if (NewPassword != ConfirmPassword)
                { ErrorMessage = Resources.Web_User_PasswordsDoNotMatch; return; }
                var currentPasswordError = await VerifyCurrentPasswordAsync(SelectedUser);
                if (currentPasswordError is not null) { ErrorMessage = currentPasswordError; return; }
                await _userRepo.UpdatePasswordAsync(SelectedUser.Id,
                    _passwordService.HashPassword(NewPassword),
                    _ntHashProtector.Protect(_passwordService.ComputeNtHash(NewPassword)),
                    changedByUser: true);
                await PublishAsync(NotificationEvents.PasswordChanged(
                    SelectedUser.Id, SelectedUser.Name, SelectedUser.Username));
                InvalidateCachedWebDavLogins(SelectedUser.Id);
                // The new security stamp ended every other session; keep this one.
                await RenewOwnSessionAsync(SelectedUser.Id);
            }

            await _userRepo.UpdatePersonalNamesAsync(
                SelectedUser.Id, NullIfBlank(EditUserFirstName), NullIfBlank(EditUserLastName));
            if (_photoChanged)
                await _userRepo.UpdatePhotoAsync(SelectedUser.Id, EditUserPhoto, EditUserPhotoContentType);

            await LoadTabDataAsync();
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id);
            if (SelectedUser is not null) await ResolveSelectedUserPermissionsAsync(SelectedUser);

            IsEditing = false;
            NewPassword = ""; ConfirmPassword = ""; CurrentPassword = "";
            SuccessMessage = Resources.Web_ChangesSaved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving own profile for {UserId}", SelectedUser?.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Profile picture
    // ══════════════════════════════════════════

    /// <summary>Stages an uploaded image for the next save (admin or self-service).</summary>
    public void SetEditPhoto(byte[] bytes, string contentType)
    {
        EditUserPhoto = bytes;
        EditUserPhotoContentType = contentType;
        _photoChanged = true;
    }

    /// <summary>Stages removal of the current photo for the next save.</summary>
    public void ClearEditPhoto()
    {
        EditUserPhoto = null;
        EditUserPhotoContentType = null;
        _photoChanged = true;
    }

    /// <summary>Surfaces a UI-side validation error (e.g. rejected photo) on the shared banner.</summary>
    public void ReportError(string message) => ErrorMessage = message;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
