using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Infrastructure.Services.Identity;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the "Two-factor sign-in" settings tab: the <see cref="SecondFactorPolicy"/> of the web
/// login (mode, targets, issuer) plus how many users it covers. A save that would require a
/// second factor from the saving administrator, who has none, is refused, so the admin cannot
/// lock themselves out. Gated by <see cref="ManagementPermission.ManageSystemSettings"/>,
/// re-checked on save.
/// </summary>
public sealed class SecondFactorSettingsViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    SecondFactorPolicyService policyService,
    ISecondFactorRepository factors,
    IUserRepository users,
    PrincipalDirectoryCache principals,
    IDepartmentRepository departments,
    ILogger<SecondFactorSettingsViewModel> logger,
    ToastService? toasts = null)
{
    private UserContext? _actor;

    public bool IsLoading { get; private set; } = true;
    public bool CanManage { get; private set; }
    public bool IsSaving { get; private set; }
    // Also raised as toasts: the tab's banners scroll out of view on long settings pages.
    public string? SuccessMessage { get; private set { field = value; if (value is not null) toasts?.Show(value, ToastType.Success); } }
    public string? ErrorMessage { get; private set { field = value; if (value is not null) toasts?.Show(value, ToastType.Error); } }

    /// <summary>Whether <c>KAIMO_MFA_DISABLED</c> switches the second factor off on this server.</summary>
    public bool DisabledByEnvironment => SecondFactorPolicy.DisabledByEnvironment;

    public SecondFactorMode Mode { get; set; } = SecondFactorMode.Optional;
    public bool AppliesToAll { get; set; }
    public bool RequireForManagementRoles { get; set; } = true;
    public string Issuer { get; set; } = SecondFactorPolicy.DefaultIssuer;
    public List<Guid> UserIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
    public List<Guid> RoleIds { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();

    public IReadOnlyList<PrincipalPickerItem> UserItems { get; private set; } = [];
    public IReadOnlyList<PrincipalPickerItem> GroupItems { get; private set; } = [];
    public IReadOnlyList<PrincipalPickerItem> RoleItems { get; private set; } = [];

    // Shared principal list (stale-while-revalidate); see RevalidatePrincipalsAsync.
    private PrincipalDirectory _principals = PrincipalDirectory.Empty;

    /// <summary>Background refresh of the user, group and role pickers; true when they changed.</summary>
    public async Task<bool> RevalidatePrincipalsAsync()
    {
        if (!CanManage || await principals.RevalidateSinceAsync(_principals, logger) is not { } newer)
            return false;
        ApplyPrincipals(newer);
        return true;
    }

    private void ApplyPrincipals(PrincipalDirectory directory)
    {
        _principals = directory;
        UserItems = directory.Users.ToUserItems();
        GroupItems = directory.Groups.ToGroupItems();
        RoleItems = directory.Roles.ToRoleItems();
    }
    public IReadOnlyList<PrincipalPickerItem> DepartmentItems { get; private set; } = [];

    /// <summary>Users the saved policy requires a second factor from, and how many of them have none yet.</summary>
    public int RequiredCount { get; private set; }
    public int RequiredNotEnrolledCount { get; private set; }
    public int EnrolledCount { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            CanManage = await HasPermissionAsync();
            if (!CanManage) return;

            Apply(await policyService.GetPolicyAsync());
            ApplyPrincipals(await principals.GetAsync());
            DepartmentItems = (await departments.GetAllAsync()).OrderBy(d => d.Name).ToDepartmentItems();
            await CountAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the second-factor settings");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearMessages()
    {
        SuccessMessage = null;
        ErrorMessage = null;
    }

    public async Task SaveAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        var policy = Build();
        IsSaving = true;
        try
        {
            // Never require a second factor from the saving admin before they have one: the
            // next revalidation would end this session and the login would demand an enrollment.
            if (await policyService.IsRequiredAsync(_actor!, policy)
                && (await factors.GetAsync(_actor!.User.Id))?.ConfirmedAtUtc is null)
            {
                ErrorMessage = R("Web_SecondFactor_Settings_SelfNotEnrolled");
                return;
            }

            await policyService.SavePolicyAsync(policy);
            logger.LogInformation(
                "Second-factor policy saved: {Mode}, all {AppliesToAll}, management roles {Management}, " +
                "{Users} users, {Groups} groups, {Roles} roles, {Departments} departments",
                policy.Mode, policy.AppliesToAll, policy.RequireForManagementRoles,
                policy.UserIds.Count, policy.GroupIds.Count, policy.RoleIds.Count, policy.DepartmentIds.Count);
            await CountAsync();
            SuccessMessage = R("Web_ChangesSaved");
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the second-factor settings");
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Apply(SecondFactorPolicy policy)
    {
        Mode = policy.Mode;
        AppliesToAll = policy.AppliesToAll;
        RequireForManagementRoles = policy.RequireForManagementRoles;
        Issuer = policy.Issuer;
        UserIds = policy.UserIds.ToList();
        GroupIds = policy.GroupIds.ToList();
        RoleIds = policy.RoleIds.ToList();
        DepartmentIds = policy.DepartmentIds.ToList();
    }

    private SecondFactorPolicy Build() => new()
    {
        Mode = Mode,
        AppliesToAll = AppliesToAll,
        RequireForManagementRoles = RequireForManagementRoles,
        Issuer = (Issuer ?? "").Trim() is { Length: > 0 } issuer
            ? issuer[..Math.Min(issuer.Length, SecondFactorPolicy.MaxIssuerLength)]
            : SecondFactorPolicy.DefaultIssuer,
        UserIds = UserIds.Distinct().ToList(),
        GroupIds = GroupIds.Distinct().ToList(),
        RoleIds = RoleIds.Distinct().ToList(),
        DepartmentIds = DepartmentIds.Distinct().ToList(),
    };

    // ponytail: one context + policy evaluation per user (a few queries each); fine for the
    // user counts this server targets, a batched evaluation if installs grow into thousands.
    private async Task CountAsync()
    {
        var enrolled = await factors.GetConfirmedUserIdsAsync();
        int required = 0, missing = 0;
        foreach (var user in await users.GetAllAsync())
        {
            var context = await userContextFactory.CreateAsync(user);
            if (!await policyService.IsRequiredAsync(context)) continue;
            required++;
            if (!enrolled.Contains(user.Id)) missing++;
        }
        RequiredCount = required;
        RequiredNotEnrolledCount = missing;
        EnrolledCount = enrolled.Count;
    }

    private async Task<bool> HasPermissionAsync()
        => _actor is not null && await mgmtAuth.HasGlobalPermissionAsync(_actor, ManagementPermission.ManageSystemSettings);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
