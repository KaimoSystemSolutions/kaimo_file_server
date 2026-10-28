using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class DepartmentViewModel
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

    /// <summary>Whether the selected department may be deleted (EditDepartment on its parent).</summary>
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

    // -- Create --
    public string CreateName { get; set; } = "";
    public string CreateDescription { get; set; } = "";
    public Guid? CreateParentId { get; set; }

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

        await LoadCreateParentOptionsAsync();
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
            CanDelete = false;
            return;
        }

        Selected = dept;
        await LoadDetailAsync(dept);
        CanDelete = await CanDeleteDepartmentAsync(dept);
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
        // Top level needs global authority; scoped admins start below their own department.
        CreateParentId = CanCreateTopLevel ? null : CreateParentOptions.FirstOrDefault()?.Id;
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

        if (await IsNameTakenAsync(CreateName.Trim(), exceptId: null))
        { ErrorMessage = Resources.Web_Dept_NameTaken; return; }

        // "No parent" already means "directly below Global".
        var parentId = CreateParentId == WellKnownGUIDs.DEPARTMENT_GLOBAL ? null : CreateParentId;
        if (await ValidateNewParentAsync(parentId) is { } parentError)
        { ErrorMessage = parentError; return; }

        try
        {
            IsSaving = true;
            var dept = new Department(
                CreateName.Trim(),
                string.IsNullOrWhiteSpace(CreateDescription) ? null : CreateDescription.Trim(),
                parentId);
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

    // Department names are unique (database index); checking first gives a clear message
    // instead of the generic save error the constraint violation would cause.
    private async Task<bool> IsNameTakenAsync(string name, Guid? exceptId)
        => await _departmentRepo.GetByNameAsync(name) is { } existing && existing.Id != exceptId;

    // ══════════════════════════════════════════
    //  Delete
    // ══════════════════════════════════════════

    private static string AssignmentsBeyondOwnRightsMessage
        => Resources.ResourceManager.GetString("Web_Dept_AssignmentsBeyondOwnRights")
           ?? "This department has role assignments with rights you do not hold yourself. Only an administrator holding those rights can delete it.";

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

        // Re-read: the parent decides who may delete, and the loaded object may be stale.
        var stored = await _departmentRepo.GetByIdAsync(Selected.Id);
        if (stored is null || !await CanDeleteDepartmentAsync(stored))
        { ErrorMessage = Resources.Web_Error_NoPermission; return; }

        // The department's role assignments go with it: never revoke one the actor could not hold.
        if (!await _roleAssignments.CoversAssignmentsAtAsync(_actorContext, ScopeType.Department, stored.Id))
        { ErrorMessage = AssignmentsBeyondOwnRightsMessage; return; }

        try
        {
            IsSaving = true;

            // Children, groups and shares move up to the deleted department's parent (Global for a
            // top-level department), so they stay under the authority that allowed the delete.
            // The children's own assignments stay.
            foreach (var child in await _departmentRepo.GetChildrenAsync(stored.Id))
            {
                child.ParentDepartmentId = stored.ParentDepartmentId;
                await _departmentRepo.UpdateAsync(child);
            }

            var newHomeId = stored.ParentDepartmentId ?? WellKnownGUIDs.DEPARTMENT_GLOBAL;
            var deptGroups = await _departmentRepo.GetGroupsAsync(Selected.Id);
            foreach (var group in deptGroups)
            {
                group.DepartmentId = newHomeId;
                await _groupRepo.UpdateAsync(group);
            }

            var deptShares = await _departmentRepo.GetSharesAsync(Selected.Id);
            foreach (var share in deptShares)
            {
                share.DepartmentId = newHomeId;
                await _shareRepo.UpdateAsync(share);
            }

            await _departmentRepo.DeleteAsync(Selected.Id);
            // Its role assignments would otherwise be left behind pointing at nothing.
            await _assignmentRepo.DeleteByScopeAsync(ScopeType.Department, Selected.Id);

            Selected = null;
            Members = []; Groups = []; Shares = [];
            PermInfo = null;
            CanDelete = false;
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

}
