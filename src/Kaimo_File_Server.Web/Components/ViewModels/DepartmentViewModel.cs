using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
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
    private readonly IManagementAuthService _mgmtAuth;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<DepartmentViewModel> _logger;

    public DepartmentViewModel(
        IDepartmentRepository departmentRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IShareRepository shareRepo,
        IRoleRepository roleRepo,
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
        _mgmtAuth = mgmtAuth;
        _userContextFactory = userContextFactory;
        _authState = authState;
        _logger = logger;
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

    // -- Lists --
    public List<Department> Departments { get; private set; } = [];

    // -- Selection --
    public Department? Selected { get; set; }
    public List<User> Members { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public List<ShareDefinition> Shares { get; private set; } = [];
    public EffectivePermissionInfo? PermInfo { get; private set; }

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

        CanAccessPage = canView || CanEdit || IsGlobalAdmin;
    }

    private async Task LoadDepartmentsAsync()
    {
        if (IsGlobalAdmin)
        {
            Departments = (await _departmentRepo.GetAllAsync())
                .OrderBy(d => d.Name).ToList();
        }
        else
        {
            var result = await _mgmtAuth.GetAuthorizedDepartmentIdsAsync(
                _actorContext!, ManagementPermission.ViewDepartment);

            if (result.IsUnrestricted)
                Departments = (await _departmentRepo.GetAllAsync())
                    .OrderBy(d => d.Name).ToList();
            else
            {
                var all = await _departmentRepo.GetAllAsync();
                Departments = all.Where(d => result.ScopeIds.Contains(d.Id))
                    .OrderBy(d => d.Name).ToList();
            }
        }
    }

    // ══════════════════════════════════════════
    //  Selection
    // ══════════════════════════════════════════

    public async Task SelectAsync(Department dept)
    {
        CancelEdit(); CancelCreate();
        SuccessMessage = null;

        if (Selected?.Id == dept.Id)
        {
            Selected = null;
            Members = []; Groups = []; Shares = [];
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

        // Available parents (exclude self + descendants to prevent cycles)
        var allDepts = await _departmentRepo.GetAllAsync();
        var descendantIds = await _departmentRepo.GetDescendantIdsAsync(Selected.Id);
        descendantIds.Add(Selected.Id);
        AvailableParents = allDepts
            .Where(d => !descendantIds.Contains(d.Id))
            .OrderBy(d => d.Name).ToList();

        // Members (M:N — DepartmentUser)
        var allUsers = (await _userRepo.GetAllAsync()).OrderBy(u => u.Name).ToList();
        var memberIds = Members.Select(u => u.Id).ToHashSet();
        EditMembers = allUsers
            .Select(u => new CheckboxItem<User>(u, memberIds.Contains(u.Id))).ToList();

        // Groups (direct FK: Group.DepartmentId)
        var allGroups = (await _groupRepo.GetAllAsync()).OrderBy(g => g.Name).ToList();
        EditGroups = allGroups
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
            Selected.ParentDepartmentId = EditParentId;
            Selected.DefaultFilePermission = EditHasOwnPermission
                ? (long)EditDefaultPermission
                : null;

            await _departmentRepo.UpdateAsync(Selected);

            // -- Members diff (M:N via DepartmentUser) --
            var desiredUserIds = EditMembers
                .Where(m => m.IsChecked).Select(m => m.Item.Id).ToHashSet();
            var currentUserIds = Members.Select(u => u.Id).ToHashSet();

            foreach (var userId in desiredUserIds.Except(currentUserIds))
                await _departmentRepo.AddUserAsync(Selected.Id, userId);
            foreach (var userId in currentUserIds.Except(desiredUserIds))
                await _departmentRepo.RemoveUserAsync(Selected.Id, userId);

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
            SuccessMessage = Resources.Web_Dept_Saved;
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
                CreateParentId);
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
        if (!parentId.HasValue) return "— Keine (Oberste Ebene)";
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
        foreach (var d in Departments.Where(d => Departments.Any(c => c.ParentDepartmentId == d.Id)))
            _collapsedIds.Add(d.Id);
    }

    public void ExpandAll() => _collapsedIds.Clear();

    public List<DepartmentNode> GetTreeOrderedDepartments()
    {
        var result = new List<DepartmentNode>();
        var deptIds = Departments.Select(d => d.Id).ToHashSet();

        var roots = Departments
            .Where(d => !d.ParentDepartmentId.HasValue
                        || !deptIds.Contains(d.ParentDepartmentId.Value))
            .OrderBy(d => d.Name)
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
            .Where(d => d.ParentDepartmentId == dept.Id)
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