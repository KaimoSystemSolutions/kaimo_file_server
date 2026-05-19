using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public class AclEditorViewModel
{
    private readonly IAclRepository _aclRepo;
    private readonly IFileMetadataRepository _metaRepo;
    private readonly IUserRepository _userRepo;
    private readonly IGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly ILogger<AclEditorViewModel> _logger;
    public Guid ShareId { get; private set; }

    public AclEditorViewModel(
        IAclRepository aclRepo,
        IFileMetadataRepository metaRepo,
        IUserRepository userRepo,
        IGroupRepository groupRepo,
        IRoleRepository roleRepo,
        ILogger<AclEditorViewModel> logger)
    {
        _aclRepo = aclRepo;
        _metaRepo = metaRepo;
        _userRepo = userRepo;
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _logger = logger;
    }

    // -- State --

    public Guid FileMetadataId { get; private set; }
    public string Path { get; private set; } = "";
    public bool IsLoaded { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    public List<AccessEntry> Entries { get; private set; } = [];
    public List<User> AllUsers { get; private set; } = [];
    public List<Group> AllGroups { get; private set; } = [];
    public List<Role> AllRoles { get; private set; } = [];

    // -- New Entry State --

    public bool IsAddingEntry { get; set; }
    public string NewPrincipalType { get; set; } = "user";  // "user", "group", "role"
    public Guid? NewPrincipalId { get; set; }
    public AclEntryType NewEntryType { get; set; } = AclEntryType.Allow;
    public FilePermission NewPermissions { get; set; } = FilePermission.None;
    public AclInheritance NewInheritance { get; set; } = AclInheritance.Everything;

    // -- Edit State --

    public Guid? EditingEntryId { get; set; }

    // -- Load --

    /// <summary>
    /// Lädt oder erstellt die FileMetadata für den gegebenen Pfad und lädt die ACL-Einträge.
    /// </summary>
    public async Task LoadAsync(string path, Guid shareId, bool isDirectory = true)
    {
        IsLoaded = false;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            Path = path;
            ShareId = shareId;

            Console.WriteLine($"[AclEditor DEBUG] LoadAsync path='{path}', shareId={shareId}, isDirectory={isDirectory}");

            var meta = await _metaRepo.GetOrCreateAsync(path, isDirectory, Guid.Empty, shareId);

            Console.WriteLine($"[AclEditor DEBUG] meta.Id={meta.Id}, meta.Path='{meta.Path}', meta.ShareId={meta.ShareId}");

            FileMetadataId = meta.Id;
            Entries = await _aclRepo.GetByFileMetadataIdAsync(meta.Id);

            Console.WriteLine($"[AclEditor DEBUG] Entries count={Entries.Count}");

            AllUsers = (await _userRepo.GetAllAsync()).ToList();
            AllGroups = (await _groupRepo.GetAllAsync()).ToList();
            AllRoles = (await _roleRepo.GetAllAsync()).ToList();

            Console.WriteLine($"[AclEditor DEBUG] Users={AllUsers.Count}, Groups={AllGroups.Count}, Roles={AllRoles.Count}");

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der ACL für '{Path}'", path);
            Console.WriteLine($"[AclEditor DEBUG] EXCEPTION: {ex}");
            ErrorMessage = "Fehler beim Laden der Berechtigungen.";
        }
    }

    // -- Add --

    public void StartAddEntry()
    {
        IsAddingEntry = true;
        NewPrincipalType = "user";
        NewPrincipalId = null;
        NewEntryType = AclEntryType.Allow;
        NewPermissions = FilePermission.None;
        NewInheritance = AclInheritance.Everything;
        EditingEntryId = null;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    public void CancelAddEntry()
    {
        IsAddingEntry = false;
    }

    public async Task<bool> AddEntryAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (NewPrincipalId is null)
        { ErrorMessage = "Bitte einen Benutzer, eine Gruppe oder Rolle auswählen."; return false; }

        if (NewPermissions == FilePermission.None)
        { ErrorMessage = "Bitte mindestens eine Berechtigung auswählen."; return false; }

        // Prüfen ob bereits ein Eintrag für diesen Principal mit gleichem EntryType existiert
        var duplicate = Entries.FirstOrDefault(e =>
            e.PrincipalId == NewPrincipalId.Value && e.EntryType == NewEntryType);
        if (duplicate is not null)
        {
            ErrorMessage = "Es existiert bereits ein Eintrag für diesen Principal mit diesem Typ. Bitte bearbeite den bestehenden Eintrag.";
            return false;
        }

        try
        {
            var entry = new AccessEntry(
                NewPrincipalId.Value,
                NewEntryType,
                NewPermissions,
                NewInheritance)
            {
                FileMetadataId = FileMetadataId
            };

            await _aclRepo.AddAsync(entry);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);

            SuccessMessage = "Berechtigung hinzugefügt.";
            IsAddingEntry = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Hinzufügen der Berechtigung");
            ErrorMessage = "Fehler beim Hinzufügen.";
            return false;
        }
    }

    // -- Edit --

    public void StartEditEntry(AccessEntry entry)
    {
        EditingEntryId = entry.Id;
        NewPrincipalId = entry.PrincipalId;
        NewEntryType = entry.EntryType;
        NewPermissions = entry.Permissions;
        NewInheritance = entry.Inheritance;
        IsAddingEntry = false;
        ErrorMessage = null;
        SuccessMessage = null;

        // Principal-Typ bestimmen
        if (AllUsers.Any(u => u.Id == entry.PrincipalId))
            NewPrincipalType = "user";
        else if (AllGroups.Any(g => g.Id == entry.PrincipalId))
            NewPrincipalType = "group";
        else
            NewPrincipalType = "role";
    }

    public void CancelEditEntry()
    {
        EditingEntryId = null;
    }

    public async Task<bool> SaveEditEntryAsync()
    {
        if (EditingEntryId is null) return false;

        ErrorMessage = null;
        SuccessMessage = null;

        if (NewPermissions == FilePermission.None)
        { ErrorMessage = "Bitte mindestens eine Berechtigung auswählen."; return false; }

        try
        {
            var entry = Entries.FirstOrDefault(e => e.Id == EditingEntryId.Value);
            if (entry is null) { ErrorMessage = "Eintrag nicht gefunden."; return false; }

            entry.EntryType = NewEntryType;
            entry.Permissions = NewPermissions;
            entry.Inheritance = NewInheritance;

            await _aclRepo.UpdateAsync(entry);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);

            SuccessMessage = "Berechtigung aktualisiert.";
            EditingEntryId = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Aktualisieren der Berechtigung");
            ErrorMessage = "Fehler beim Speichern.";
            return false;
        }
    }

    // -- Delete --

    public async Task<bool> DeleteEntryAsync(Guid entryId)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            await _aclRepo.DeleteAsync(entryId);
            Entries = await _aclRepo.GetByFileMetadataIdAsync(FileMetadataId);
            SuccessMessage = "Berechtigung entfernt.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Entfernen der Berechtigung");
            ErrorMessage = "Fehler beim Entfernen.";
            return false;
        }
    }

    // -- Helpers --

    public string GetPrincipalDisplayName(Guid principalId)
    {
        var user = AllUsers.FirstOrDefault(u => u.Id == principalId);
        if (user is not null) return user.Name ?? user.Username;

        var group = AllGroups.FirstOrDefault(g => g.Id == principalId);
        if (group is not null) return group.Name;

        var role = AllRoles.FirstOrDefault(r => r.Id == principalId);
        if (role is not null) return role.Name;

        return principalId.ToString()[..8] + "…";
    }

    public string GetPrincipalType(Guid principalId)
    {
        if (AllUsers.Any(u => u.Id == principalId)) return "user";
        if (AllGroups.Any(g => g.Id == principalId)) return "group";
        if (AllRoles.Any(r => r.Id == principalId)) return "role";
        return "unknown";
    }

    public string GetPrincipalIcon(Guid principalId) => GetPrincipalType(principalId) switch
    {
        "user" => "user",
        "group" => "group",
        "role" => "role",
        _ => "unknown"
    };

    // -- Permission Helpers --

    public bool HasPermission(FilePermission flags, FilePermission flag)
        => (flags & flag) != 0;

    public FilePermission TogglePermission(FilePermission flags, FilePermission flag)
        => flags ^ flag;

    public FilePermission ApplyShortcut(FilePermission flags, FilePermission shortcut)
    {
        // Wenn alle Bits des Shortcuts gesetzt sind, entferne sie. Sonst füge sie hinzu.
        if ((flags & shortcut) == shortcut)
            return flags & ~shortcut;
        return flags | shortcut;
    }

    public bool IsShortcutFullySet(FilePermission flags, FilePermission shortcut)
        => (flags & shortcut) == shortcut;
}