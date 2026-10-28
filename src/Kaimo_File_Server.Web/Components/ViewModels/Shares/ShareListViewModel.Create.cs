using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    // -- Create --

    public bool IsCreating { get; set; }
    public string NewShareName { get; set; } = "";
    public string NewSharePoolPath { get; set; }
    public string? CreateErrorMessage { get; private set; }

    // Departments where the actor holds CreateShares (resolved in LoadAsync).
    private AuthorizedScopeResult _createShareScope = AuthorizedScopeResult.None();

    /// <summary>
    /// Departments a new share may be created in: those where the actor holds
    /// <see cref="ManagementPermission.CreateShares"/> (Global only with global authority).
    /// </summary>
    public List<Department> CreateShareDepartments => Departments
        .Where(d => _createShareScope.IsUnrestricted || _createShareScope.ScopeIds.Contains(d.Id))
        .ToList();

    /// <summary>The department the new share is created in; checked again on create.</summary>
    public Guid CreateShareDepartmentId { get; set; } = WellKnownGUIDs.DEPARTMENT_GLOBAL;

    /// <summary>Opens the create form; global actors start in Global, scoped ones in their first department.</summary>
    public void StartCreate()
    {
        IsCreating = true;
        CreateErrorMessage = null;
        CreateShareDepartmentId = _createShareScope.IsUnrestricted
            ? WellKnownGUIDs.DEPARTMENT_GLOBAL
            : CreateShareDepartments.FirstOrDefault()?.Id ?? WellKnownGUIDs.DEPARTMENT_GLOBAL;
    }

    // -- Create --

    public async Task<bool> CreateShareAsync()
    {
        CreateErrorMessage = null;
        var name = NewShareName.Trim();
        if (!await CreateShareCoreAsync(name, NewSharePoolPath, CreateShareDepartmentId, adoptExistingFolder: false))
            return false;

        NewShareName = "";
        NewSharePoolPath = _storagePools.FirstOrDefault() ?? string.Empty;
        IsCreating = false;
        await LoadAsync();
        return true;
    }

    /// <summary>
    /// Creates the share row in <paramref name="departmentId"/>, its root metadata and the
    /// owner/admin ACLs. The actor needs <see cref="ManagementPermission.CreateShares"/> on that
    /// department (Global: global authority). With <paramref name="adoptExistingFolder"/> the
    /// folder must already exist in the pool (an unreferenced folder becomes a share and keeps its
    /// data); otherwise it must not exist and is created. Errors go to <see cref="CreateErrorMessage"/>.
    /// </summary>
    private async Task<bool> CreateShareCoreAsync(
        string name, string poolCandidate, Guid departmentId, bool adoptExistingFolder)
    {
        if (!await CanCreateShareInAsync(departmentId))
        { CreateErrorMessage = Resources.Web_Error_NoPermission; return false; }

        if (!ValidateShareName(name, out var error))
        { CreateErrorMessage = error; return false; }

        try
        {
            var poolPath = GetConfiguredPoolPath(poolCandidate);
            if (poolPath is null)
            {
                CreateErrorMessage = Resources.Web_Error_StoragePoolRequired;
                return false;
            }

            var existing = await _shareRepo.GetByNameAsync(name);
            if (existing is not null)
            { CreateErrorMessage = Resources.Web_Error_ShareExists; return false; }

            // Den Ersteller ZUERST auflösen. Ohne gültigen Owner darf kein Share
            // entstehen, sonst bleibt ein verwaister Share ohne Owner-ACL zurück.
            // Identity?.Name-Guard vermeidet den bisherigen Null-Deref.
            var state = await _authState.GetAuthenticationStateAsync();
            var username = state.User.Identity?.Name;
            var user = string.IsNullOrEmpty(username)
                ? null
                : await _userRepo.GetByUsernameAsync(username);

            if (user is null)
            {
                _logger.LogWarning(
                    "Share creation aborted: could not resolve creator (user='{User}')", username);
                CreateErrorMessage = Resources.Web_Error_CreateShareFailed;
                return false;
            }

            var sharePath = BuildSharePath(poolPath, name);
            if (adoptExistingFolder)
            {
                if (!Directory.Exists(sharePath))
                {
                    CreateErrorMessage = Resources.Web_Error_SharePathMissing;
                    return false;
                }
            }
            else if (Directory.Exists(sharePath) || File.Exists(sharePath))
            {
                CreateErrorMessage = Resources.Web_Error_StoragePoolDestinationExists;
                return false;
            }

            var share = new ShareDefinition(name, sharePath, departmentId);
            Directory.CreateDirectory(share.Path);
            await _shareRepo.CreateAsync(share);

            // Root-FileMetadata persistieren (Path == "", Konvention aus
            // ShareRelativePath) und die Owner-FullControl-ACL daran hängen.
            // GetOrCreateAsync schreibt die Metadata-Zeile tatsächlich in die DB —
            // vorher wurde nur die ACL angelegt, deren FileMetadata nie existierte.
            var rootMeta = await _metaRepo.GetOrCreateAsync(
                "", isDirectory: true, userId: user.Id, shareId: share.Id);

            // Everything (ThisFolder | SubFolders | SubFiles | AllDescendants) — die
            // Owner-Regel MUSS auch für den Share-Root selbst gelten. Mit nur
            // AllDescendants greift sie ausschließlich für Unterelemente, wodurch der
            // Root-Listing-Check (FileService.ListAsync) selbst für den Ersteller
            // fehlschlägt und der Filebrowser "Zugriff verweigert" zeigt.
            var newAcl = new AccessEntry(
                user.Id,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.Everything)
            {
                FileMetadataId = rootMeta.Id
            };

            await _aclRepo.AddAsync(newAcl);

            var adminAcl = new AccessEntry(
                WellKnownGUIDs.ROLE_ADMIN,
                AclEntryType.Allow,
                FilePermission.FullControl,
                AclInheritance.Everything)
            {
                FileMetadataId = rootMeta.Id
            };

            await _aclRepo.AddAsync(adminAcl);

            _logger.LogInformation(adoptExistingFolder
                ? "Share '{ShareName}' created from existing folder"
                : "Share '{ShareName}' created", name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating share '{ShareName}'", name);
            CreateErrorMessage = Resources.Web_Error_CreateShareFailed;
            return false;
        }
    }

    private async Task<bool> CanCreateShareInAsync(Guid departmentId)
    {
        // Resolve the actor even when the page has not been loaded yet (default deny otherwise).
        var actor = _actorContext;
        if (actor is null)
        {
            var username = (await _authState.GetAuthenticationStateAsync()).User.Identity?.Name;
            actor = string.IsNullOrEmpty(username) ? null : await _userContextFactory.CreateByUsernameAsync(username);
        }
        if (actor is null)
            return false;
        // A forged id must not leave a share pointing at a department that does not exist.
        if (departmentId != WellKnownGUIDs.DEPARTMENT_GLOBAL && _departmentRepo is not null
            && await _departmentRepo.GetByIdAsync(departmentId) is null)
            return false;
        return await _mgmtAuth.CanManageDepartmentAsync(actor, departmentId, ManagementPermission.CreateShares);
    }
}
