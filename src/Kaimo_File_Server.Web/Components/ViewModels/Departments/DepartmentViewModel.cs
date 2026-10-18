using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.DynamicHelpers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class DepartmentViewModel
{
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IShareRepository _shareRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IScopedRoleAssignmentRepository _assignmentRepo;
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<DepartmentViewModel> _logger;
    private readonly RoleAssignmentService _roleAssignments;

    public DepartmentViewModel(
        IDepartmentRepository departmentRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IShareRepository shareRepo,
        IRoleRepository roleRepo,
        IScopedRoleAssignmentRepository assignmentRepo,
        IManagementAuthService mgmtAuth,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authState,
        ILogger<DepartmentViewModel> logger)
    {
        _departmentRepo = departmentRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _shareRepo = shareRepo;
        _roleRepo = roleRepo;
        _assignmentRepo = assignmentRepo;
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
        _roleAssignments = new RoleAssignmentService(assignmentRepo, roleRepo, userRepo, groupRepo, mgmtAuth);
    }

    // ══════════════════════════════════════════
    //  State
    // ══════════════════════════════════════════

    private UserContext? _actorContext;

    public bool IsLoading { get; private set; }
    public bool IsEditing { get; private set; }
    public bool IsSaving { get; private set; }
    public bool IsCreating { get; set; }
    public bool IsConfirmingDelete { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    // -- Permissions --
    public bool CanAccessPage { get; private set; }
    public bool IsGlobalAdmin { get; private set; }
    public bool CanEdit { get; private set; }
    public bool CanDelete { get; private set; }

    /// <summary>Whether the actor may assign roles somewhere; gates the add/remove controls of
    /// the department's role assignments. The per-assignment limit is enforced by
    /// <see cref="IManagementAuthService.CanAssignRoleAsync"/>.</summary>
    public bool CanAssignRoles { get; private set; }

    // -- Lists --
    public List<Department> Departments { get; private set; } = [];

    // -- Selection --
    public Department? Selected { get; set; }
    public List<User> Members { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public List<ShareDefinition> Shares { get; private set; } = [];
    public EffectivePermissionInfo? PermInfo { get; private set; }

    /// <summary>Role assignments that apply to the selected department: its own (editable) and,
    /// read-only with <see cref="ScopedAssignmentDisplayItem.InheritedFrom"/> set, those of its
    /// ancestors and of the Global department. On the Global department these are the
    /// system-wide assignments.</summary>
    public List<ScopedAssignmentDisplayItem> RoleAssignments { get; private set; } = [];

    // -- Add department-scoped role assignment --
    public bool IsAddingAssignment { get; private set; }
    public Guid? NewAssignmentRoleId { get; set; }
    public Guid? NewAssignmentPrincipalId { get; set; }
    public List<Role> AllRoles { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];

    // -- Create --
    public string CreateName { get; set; } = "";
    public string CreateDescription { get; set; } = "";
    public Guid? CreateParentId { get; set; }

    // -- Edit --
    public string EditName { get; set; } = "";
    public string EditDescription { get; set; } = "";
    public Guid? EditParentId { get; set; }
    public bool EditHasOwnPermission { get; set; }
    public FilePermission EditDefaultPermission { get; set; } = FilePermission.None;
    /// <summary>When false the department uses its automatic color (<see cref="Department.Color"/> = null).</summary>
    public bool EditUseCustomColor { get; set; }
    /// <summary>Current picker value; only persisted when <see cref="EditUseCustomColor"/> is true.</summary>
    public string EditColor { get; set; } = "";
    public List<CheckboxItem<User>> EditMembers { get; private set; } = [];
    public List<CheckboxItem<Group>> EditGroups { get; private set; } = [];
    public List<CheckboxItem<ShareDefinition>> EditShares { get; private set; } = [];
    public List<Department> AvailableParents { get; private set; } = [];

    // ══════════════════════════════════════════
    //  FilePermission display metadata for the department default-permission UI
    // ══════════════════════════════════════════

    // Built on each access so the labels resolve against the current UI culture
    // (CurrentUICulture is set per request, so a static-readonly list would freeze
    // the language captured at type-load time).
    public static List<DeptPermFlag> PermFlags =>
    [
        new(Resources.Web_DeptPerm_Read, Resources.Web_DeptPerm_ReadDesc,
            FilePermission.ReadAll),
        new(Resources.Web_DeptPerm_Write, Resources.Web_DeptPerm_WriteDesc,
            FilePermission.CreateWriteData | FilePermission.CreateAppendData
            | FilePermission.WriteAttributes | FilePermission.WriteExtAttributes),
        new(Resources.Web_DeptPerm_Delete, Resources.Web_DeptPerm_DeleteDesc,
            FilePermission.Delete | FilePermission.DeleteSubItems),
        new(Resources.Web_DeptPerm_Admin, Resources.Web_DeptPerm_AdminDesc,
            FilePermission.AdminAll),
    ];

    public bool HasPermFlag(FilePermission flag)
        => (EditDefaultPermission & flag) == flag;

    public void TogglePermFlag(FilePermission flag, bool value)
    {
        if (value) EditDefaultPermission |= flag;
        else EditDefaultPermission &= ~flag;
    }

    // ══════════════════════════════════════════
    //  Load
    // ══════════════════════════════════════════

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            _actorContext = await BuildActorContextAsync();
            if (_actorContext == null)
            {
                CanAccessPage = false;
                ErrorMessage = Resources.Web_Error_NotLoggedIn;
                return;
            }

            await ResolvePermissionsAsync();
            if (!CanAccessPage) return;

            await LoadDepartmentsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the department management");
            ErrorMessage = Resources.Web_Error_LoadFailedGeneric;
        }
        finally { IsLoading = false; }
    }

    private async Task<UserContext?> BuildActorContextAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    private async Task ResolvePermissionsAsync()
    {
        if (_actorContext == null) return;

        IsGlobalAdmin = await _mgmtAuth.HasGlobalPermissionAsync(
            _actorContext, ManagementPermission.FullAdmin);

        var canView = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.ViewDepartment);

        CanEdit = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.EditDepartment);

        CanDelete = IsGlobalAdmin;

        CanAssignRoles = await _mgmtAuth.HasAnyPermissionAsync(
            _actorContext, ManagementPermission.AssignRoles);

        CanAccessPage = canView || CanEdit || IsGlobalAdmin;
    }

    private async Task LoadDepartmentsAsync()
    {
        if (IsGlobalAdmin)
        {
            Departments = (await _departmentRepo.GetAllAsync())
                .OrderByGlobalFirst().ToList();
        }
        else
        {
            var result = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
                _actorContext!, ManagementPermission.ViewDepartment);

            if (result.IsUnrestricted)
                Departments = (await _departmentRepo.GetAllAsync())
                    .OrderByGlobalFirst().ToList();
            else
            {
                var all = await _departmentRepo.GetAllAsync();
                Departments = all.Where(d => result.ScopeIds.Contains(d.Id))
                    .OrderByGlobalFirst().ToList();
            }
        }
    }

    // ══════════════════════════════════════════
    //  Selection
    // ══════════════════════════════════════════

    public async Task SelectAsync(Department dept)
    {
        CancelEdit(); CancelCreate(); CancelAddAssignment();
        SuccessMessage = null;

        if (Selected?.Id == dept.Id)
        {
            Selected = null;
            Members = []; Groups = []; Shares = []; RoleAssignments = [];
            PermInfo = null;
            return;
        }

        Selected = dept;
        await LoadDetailAsync(dept);
    }

    private async Task LoadDetailAsync(Department dept)
    {
        Members = (await _departmentRepo.GetUsersAsync(dept.Id))
            .OrderBy(u => u.Name).ToList();

        // Groups with DepartmentId == this department (direct FK)
        Groups = (await _departmentRepo.GetGroupsAsync(dept.Id))
            .OrderBy(g => g.Name).ToList();

        // Shares with DepartmentId == this department (direct FK)
        Shares = (await _departmentRepo.GetSharesAsync(dept.Id))
            .OrderBy(s => s.Name).ToList();

        PermInfo = await ResolveEffectivePermissionAsync(dept.Id);
        await LoadRoleAssignmentsAsync(dept.Id);
    }

    // ══════════════════════════════════════════
    //  Department-scoped role assignments
    // ══════════════════════════════════════════

    private async Task LoadRoleAssignmentsAsync(Guid departmentId)
    {
        var users = (await _userRepo.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);
        var groups = (await _groupRepo.GetAllAsync()).ToDictionary(g => g.Id, g => g.Name);
        var roles = (await _roleRepo.GetAllAsync()).ToDictionary(r => r.Id, r => r.Name);
        var globalName = (await _departmentRepo.GetByIdAsync(WellKnownGUIDs.DEPARTMENT_GLOBAL))?.Name ?? "Global";

        ScopedAssignmentDisplayItem ToItem(ScopedRoleAssignment a, string scopeName, string? inheritedFrom) => new(
            a.Id,
            users.GetValueOrDefault(a.PrincipalId) ?? groups.GetValueOrDefault(a.PrincipalId) ?? a.PrincipalId.ToString(),
            groups.ContainsKey(a.PrincipalId),
            roles.GetValueOrDefault(a.RoleId) ?? a.RoleId.ToString(),
            a.ScopeType,
            scopeName,
            inheritedFrom);

        var items = new List<ScopedAssignmentDisplayItem>();
        if (departmentId == WellKnownGUIDs.DEPARTMENT_GLOBAL)
        {
            // The Global department is the system-wide scope.
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty))
                .Select(a => ToItem(a, globalName, null)));
        }
        else
        {
            var deptName = (await _departmentRepo.GetByIdAsync(departmentId))?.Name ?? departmentId.ToString();
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Department, departmentId))
                .Select(a => ToItem(a, deptName, null)));

            // Department scope covers descendants, and Global covers everything, so the
            // assignments of every ancestor and of Global apply here too.
            foreach (var ancestor in await _departmentRepo.GetAncestorChainAsync(departmentId))
                items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Department, ancestor.Id))
                    .Select(a => ToItem(a, ancestor.Name, ancestor.Name)));
            items.AddRange((await _assignmentRepo.GetByScopeAsync(ScopeType.Global, Guid.Empty))
                .Select(a => ToItem(a, globalName, globalName)));
        }

        // System-wide assignments lead the list, then the department's own, then inherited ones.
        RoleAssignments = items
            .OrderBy(i => i.ScopeType != ScopeType.Global).ThenBy(i => i.InheritedFrom is not null).ThenBy(i => i.RoleName).ThenBy(i => i.PrincipalName)
            .ToList();
    }

    /// <summary>Whether <paramref name="assignment"/> is one of the selected department's own
    /// (editable) assignments; on the Global department those are the system-wide ones.</summary>
    private bool IsOwnAssignment(ScopedRoleAssignment assignment)
        => Selected is not null && (Selected.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL
            ? assignment.ScopeType == ScopeType.Global
            : assignment.ScopeType == ScopeType.Department && assignment.ScopeId == Selected.Id);

    public async Task StartAddAssignmentAsync()
    {
        IsAddingAssignment = true;
        NewAssignmentRoleId = null;
        NewAssignmentPrincipalId = null;
        ErrorMessage = null;
        AllRoles = (await _roleRepo.GetAllAsync()).OrderBy(r => r.Name).ToList();
        AllUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        AllGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
    }

    public void CancelAddAssignment()
    {
        IsAddingAssignment = false;
        ErrorMessage = null;
    }

    /// <summary>Assigns the chosen role to the chosen user or group, scoped to the selected
    /// department. On the Global department this is a system-wide assignment, so the shared
    /// service also applies the Admins-group mirror.</summary>
    public async Task CreateAssignmentAsync()
    {
        if (Selected is null || _actorContext is null) return;

        var role = NewAssignmentRoleId is { } roleId ? await _roleRepo.GetByIdAsync(roleId) : null;
        if (role is null)
        { ErrorMessage = Resources.Web_Error_SelectRole; return; }
        if (NewAssignmentPrincipalId is not { } principalId)
        { ErrorMessage = Resources.Web_Error_SelectUserOrGroup; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            var error = await _roleAssignments.CreateAsync(
                _actorContext, principalId, role, ScopeType.Department, Selected.Id);
            if (error is not null) { ErrorMessage = error; return; }

            IsAddingAssignment = false;
            await LoadRoleAssignmentsAsync(Selected.Id);
            SuccessMessage = Resources.Web_Assignment_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the department role assignment");
            ErrorMessage = Resources.Web_Error_CreateAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    /// <summary>Removes one of the selected department's own assignments. Inherited ones are
    /// rejected here (they are removed where they are defined); the last-admin guard applies
    /// through the shared service.</summary>
    public async Task DeleteAssignmentAsync(Guid assignmentId)
    {
        if (Selected is null || _actorContext is null) return;

        var assignment = await _assignmentRepo.GetByIdAsync(assignmentId);
        if (assignment is null || !IsOwnAssignment(assignment))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            ErrorMessage = null;
            var error = await _roleAssignments.DeleteAsync(_actorContext, assignment);
            if (error is not null) { ErrorMessage = error; return; }

            await LoadRoleAssignmentsAsync(Selected.Id);
            SuccessMessage = Resources.Web_Assignment_Removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting department role assignment {Id}", assignmentId);
            ErrorMessage = Resources.Web_Error_DeleteAssignmentFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Effective Permission Resolution
    //  (replaces IDepartmentPermissionService)
    // ══════════════════════════════════════════

    private async Task<EffectivePermissionInfo> ResolveEffectivePermissionAsync(Guid departmentId)
    {
        var dept = await _departmentRepo.GetByIdAsync(departmentId);
        if (dept == null)
            return new EffectivePermissionInfo(FilePermission.None, false, null, null);

        if (dept.DefaultFilePermission.HasValue)
            return new EffectivePermissionInfo(
                (FilePermission)dept.DefaultFilePermission.Value, true, null, null);

        var ancestors = await _departmentRepo.GetAncestorChainAsync(departmentId);
        foreach (var ancestor in ancestors)
        {
            if (ancestor.DefaultFilePermission.HasValue)
                return new EffectivePermissionInfo(
                    (FilePermission)ancestor.DefaultFilePermission.Value,
                    false, ancestor.Id, ancestor.Name);
        }

        return new EffectivePermissionInfo(FilePermission.None, false, null, null);
    }

    // ══════════════════════════════════════════
    //  Edit
    // ══════════════════════════════════════════

    public async Task StartEditAsync()
    {
        if (Selected is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, Selected.Id, ManagementPermission.EditDepartment))
        {
            ErrorMessage = Resources.Web_Dept_NoPermissionEdit;
            return;
        }

        IsEditing = true;
        ErrorMessage = null; SuccessMessage = null;

        EditName = Selected.Name;
        EditDescription = Selected.Description ?? "";
        EditParentId = Selected.ParentDepartmentId;

        EditHasOwnPermission = Selected.DefaultFilePermission.HasValue;
        EditDefaultPermission = Selected.DefaultFilePermission.HasValue
            ? (FilePermission)Selected.DefaultFilePermission.Value
            : FilePermission.None;

        // Seed the picker with the current automatic color so enabling the toggle starts sensibly.
        EditUseCustomColor = Selected.Color != null;
        EditColor = Selected.Color ?? DepartmentPalette.AutoHex(Selected.Id);

        // Available parents (exclude self + descendants to prevent cycles). Global is not a
        // parent option: "no parent" already means "directly below Global".
        var allDepts = await _departmentRepo.GetAllAsync();
        var descendantIds = await _departmentRepo.GetDescendantIdsAsync(Selected.Id);
        descendantIds.Add(Selected.Id);
        descendantIds.Add(WellKnownGUIDs.DEPARTMENT_GLOBAL);
        AvailableParents = allDepts
            .Where(d => !descendantIds.Contains(d.Id))
            .OrderByGlobalFirst().ToList();

        // Members (M:N — DepartmentUser)
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var memberIds = Members.Select(u => u.Id).ToHashSet();
        EditMembers = allUsers
            .Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Groups (direct FK: Group.DepartmentId). The Everyone group is a live mirror of every
        // registered user and stays permanently Global — it must never be pulled into a
        // department, so it is excluded from the assignable list.
        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
        EditGroups = allGroups
            .Where(g => g.Id != WellKnownGUIDs.GROUP_EVERYONE)
            .Select(g => new CheckboxItem<Group>(g, g.DepartmentId == Selected.Id)).ToList();

        // Shares (direct FK: ShareDefinition.DepartmentId)
        var allShares = (await _shareRepo.GetAllAsync()).OrderBy(s => s.Name).ToList();
        EditShares = allShares
            .Select(s => new CheckboxItem<ShareDefinition>(s, s.DepartmentId == Selected.Id)).ToList();
    }

    public async Task SaveAsync()
    {
        if (Selected is null || _actorContext is null) return;

        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, Selected.Id, ManagementPermission.EditDepartment))
        {
            ErrorMessage = Resources.Web_Error_NoPermission;
            return;
        }

        try
        {
            IsSaving = true;
            ErrorMessage = null;

            // -- Update department entity --
            if (!string.IsNullOrWhiteSpace(EditName))
                Selected.Name = EditName.Trim();
            Selected.Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription.Trim();
            // The Global department is a pure placeholder for "the whole system": it has no
            // parent and never carries default file permissions.
            var isGlobal = IsGlobalDepartment(Selected);
            Selected.ParentDepartmentId = isGlobal || EditParentId == WellKnownGUIDs.DEPARTMENT_GLOBAL
                ? null : EditParentId;
            Selected.DefaultFilePermission = EditHasOwnPermission && !isGlobal
                ? (long)EditDefaultPermission
                : null;
            // Normalize guards the trust boundary: this value is emitted into inline CSS.
            Selected.Color = EditUseCustomColor ? DepartmentPalette.Normalize(EditColor) : null;

            await _departmentRepo.UpdateAsync(Selected);

            // -- Members diff (M:N via DepartmentUser) --
            var desiredUserIds = EditMembers
                .Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
            var currentUserIds = Members.Select(u => u.Id).ToHashSet();

            foreach (var userId in desiredUserIds.Except(currentUserIds))
                await _departmentRepo.AddUserAsync(Selected.Id, userId);
            var removedGroupMemberships = 0;
            foreach (var userId in currentUserIds.Except(desiredUserIds))
            {
                await _departmentRepo.RemoveUserAsync(Selected.Id, userId);

                var userGroups = await _userRepo.GetGroupsForUserAsync(userId);
                var remainingGroupIds = userGroups
                    .Where(group => group.DepartmentId != Selected.Id)
                    .Select(group => group.Id)
                    .ToList();

                if (remainingGroupIds.Count != userGroups.Count)
                {
                    await _userRepo.SetGroupsForUserAsync(userId, remainingGroupIds);
                    removedGroupMemberships += userGroups.Count - remainingGroupIds.Count;
                }
            }

            // -- Groups diff (direct FK: Group.DepartmentId) --
            foreach (var item in EditGroups)
            {
                var group = item.Item;
                var shouldBelong = item.IsChecked;
                var currentlyBelongs = group.DepartmentId == Selected.Id;

                if (shouldBelong && !currentlyBelongs)
                {
                    group.DepartmentId = Selected.Id;
                    await _groupRepo.UpdateAsync(group);
                }
                else if (!shouldBelong && currentlyBelongs)
                {
                    group.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                    await _groupRepo.UpdateAsync(group);
                }
            }

            // -- Shares diff (direct FK: ShareDefinition.DepartmentId) --
            foreach (var item in EditShares)
            {
                var share = item.Item;
                var shouldBelong = item.IsChecked;
                var currentlyBelongs = share.DepartmentId == Selected.Id;

                if (shouldBelong && !currentlyBelongs)
                {
                    share.DepartmentId = Selected.Id;
                    await _shareRepo.UpdateAsync(share);
                }
                else if (!shouldBelong && currentlyBelongs)
                {
                    share.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                    await _shareRepo.UpdateAsync(share);
                }
            }

            // -- Refresh --
            var refreshed = await _departmentRepo.GetByIdAsync(Selected.Id);
            if (refreshed != null) Selected = refreshed;

            await LoadDetailAsync(Selected);
            await LoadDepartmentsAsync();

            IsEditing = false;
            SuccessMessage = removedGroupMemberships == 0
                ? Resources.Web_Dept_Saved
                : $"{Resources.Web_Dept_Saved} {string.Format(Resources.Web_Dept_GroupMembershipsRemoved, removedGroupMemberships)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving department {Id}", Selected.Id);
            ErrorMessage = Resources.Web_Error_SaveFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Create
    // ══════════════════════════════════════════

    public void StartCreate()
    {
        if (!CanEdit) { ErrorMessage = Resources.Web_Error_NoPermission; return; }
        CancelEdit();
        Selected = null;
        IsCreating = true;
        CreateName = "";
        CreateDescription = "";
        CreateParentId = null;
        ErrorMessage = null; SuccessMessage = null;
    }

    public async Task CreateAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CreateName))
        { ErrorMessage = Resources.Web_Dept_NameRequired; return; }
        if (_actorContext is null) return;

        if (!await _mgmtAuth.HasAnyPermissionAsync(
                _actorContext, ManagementPermission.EditDepartment))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;
            var dept = new Department(
                CreateName.Trim(),
                string.IsNullOrWhiteSpace(CreateDescription) ? null : CreateDescription.Trim(),
                // "No parent" already means "directly below Global".
                CreateParentId == WellKnownGUIDs.DEPARTMENT_GLOBAL ? null : CreateParentId);
            await _departmentRepo.CreateAsync(dept);

            IsCreating = false;
            CreateName = ""; CreateDescription = "";
            await LoadDepartmentsAsync();
            SuccessMessage = Resources.Web_Dept_Created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating the department");
            ErrorMessage = Resources.Web_Error_CreateFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Delete
    // ══════════════════════════════════════════

    public void RequestDelete()
    {
        IsConfirmingDelete = true;
        ErrorMessage = null;
    }

    public async Task ConfirmDeleteAsync()
    {
        if (Selected is null || _actorContext is null) return;

        if (Selected.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL)
        {
            ErrorMessage = Resources.Web_Dept_CannotDeleteGlobal;
            return;
        }

        if (!await _mgmtAuth.CanManageDepartmentAsync(
                _actorContext, Selected.Id, ManagementPermission.EditDepartment))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        try
        {
            IsSaving = true;

            // Reassign orphaned groups/shares to Global before deleting
            var deptGroups = await _departmentRepo.GetGroupsAsync(Selected.Id);
            foreach (var group in deptGroups)
            {
                group.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                await _groupRepo.UpdateAsync(group);
            }

            var deptShares = await _departmentRepo.GetSharesAsync(Selected.Id);
            foreach (var share in deptShares)
            {
                share.DepartmentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
                await _shareRepo.UpdateAsync(share);
            }

            await _departmentRepo.DeleteAsync(Selected.Id);
            // Its role assignments would otherwise be left behind pointing at nothing.
            await _assignmentRepo.DeleteByScopeAsync(ScopeType.Department, Selected.Id);

            Selected = null;
            Members = []; Groups = []; Shares = [];
            PermInfo = null;
            IsConfirmingDelete = false;
            await LoadDepartmentsAsync();
            SuccessMessage = Resources.Web_Dept_Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting the department");
            ErrorMessage = Resources.Web_Error_DeleteFailed;
        }
        finally { IsSaving = false; }
    }

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    public void CancelEdit()
    {
        IsEditing = false; IsSaving = false;
        IsConfirmingDelete = false;
        ErrorMessage = null;
    }

    public void CancelCreate()
    {
        IsCreating = false;
        CreateName = ""; CreateDescription = "";
        ErrorMessage = null;
    }

    public string GetParentName(Guid? parentId)
    {
        if (!parentId.HasValue) return Resources.Web_Dept_NoParentTopLevel;
        var parent = Departments.FirstOrDefault(d => d.Id == parentId.Value);
        return parent?.Name ?? parentId.Value.ToString();
    }

    public bool IsGlobalDepartment(Department dept)
        => dept.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL;

    // ══════════════════════════════════════════
    //  Tree helpers
    // ══════════════════════════════════════════

    private readonly HashSet<Guid> _collapsedIds = [];

    public bool IsCollapsed(Guid id) => _collapsedIds.Contains(id);

    public void ToggleCollapse(Guid id)
    {
        if (!_collapsedIds.Remove(id))
            _collapsedIds.Add(id);
    }

    public void CollapseAll()
    {
        foreach (var d in Departments.Where(d => GetChildCount(d) > 0))
            _collapsedIds.Add(d.Id);
    }

    /// <summary>
    /// The parent a department is shown under: its own parent when that is visible, otherwise
    /// the Global department, which is drawn as the root of every top-level department.
    /// </summary>
    private Guid? TreeParentId(Department dept)
    {
        if (IsGlobalDepartment(dept)) return null;
        if (dept.ParentDepartmentId is { } parentId && Departments.Any(d => d.Id == parentId))
            return parentId;
        return Departments.Any(IsGlobalDepartment) ? WellKnownGUIDs.DEPARTMENT_GLOBAL : null;
    }

    /// <summary>Number of departments shown directly below <paramref name="dept"/> in the tree.</summary>
    public int GetChildCount(Department dept) => Departments.Count(d => TreeParentId(d) == dept.Id);

    public void ExpandAll() => _collapsedIds.Clear();

    public List<DepartmentNode> GetTreeOrderedDepartments()
    {
        var result = new List<DepartmentNode>();

        var roots = Departments
            .Where(d => TreeParentId(d) is null)
            .OrderBy(d => IsGlobalDepartment(d) ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();

        for (var i = 0; i < roots.Count; i++)
            AppendTree(result, roots[i], 0, i == roots.Count - 1, []);

        return result;
    }

    private void AppendTree(
        List<DepartmentNode> result,
        Department dept,
        int depth,
        bool isLast,
        bool[] ancestorContinues)
    {
        var children = Departments
            .Where(d => TreeParentId(d) == dept.Id)
            .OrderBy(d => d.Name)
            .ToList();

        result.Add(new DepartmentNode(dept, depth, children.Count > 0, isLast, ancestorContinues));

        if (_collapsedIds.Contains(dept.Id))
            return;

        for (var i = 0; i < children.Count; i++)
        {
            var childContinues = new bool[depth + 1];
            Array.Copy(ancestorContinues, childContinues, depth);
            childContinues[depth] = i < children.Count - 1;

            AppendTree(result, children[i], depth + 1, i == children.Count - 1, childContinues);
        }
    }

}

// ══════════════════════════════════════════
//  Helper Records
// ══════════════════════════════════════════

public record DeptPermFlag(string Label, string Description, FilePermission Flag);

/// <param name="Department">The department entity.</param>
/// <param name="Depth">Nesting depth (0 = root).</param>
/// <param name="HasChildren">Whether children exist (even if collapsed).</param>
/// <param name="IsLastChild">Last sibling at its level — determines └ vs ├.</param>
/// <param name="AncestorContinues">
///   Length = Depth.  Index i = true means the ancestor at depth i
///   is NOT the last sibling, so a vertical line must be drawn at column i.
/// </param>
public record DepartmentNode(
    Department Department,
    int Depth,
    bool HasChildren,
    bool IsLastChild,
    bool[] AncestorContinues);

public record EffectivePermissionInfo(
    FilePermission Permission,
    bool IsOwn,
    Guid? InheritedFromId,
    string? InheritedFromName)
{
    public bool HasNoDefault => Permission == FilePermission.None
                                && !IsOwn
                                && InheritedFromId == null;
}
