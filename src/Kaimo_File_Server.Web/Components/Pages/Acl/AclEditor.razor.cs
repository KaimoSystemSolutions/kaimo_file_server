using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Microsoft.AspNetCore.Components;

namespace Kaimo_File_Server.Web.Components.Pages.Acl;

public partial class AclEditor
{
    [Parameter] public string Path { get; set; } = "";
    [Parameter] public Guid ShareId { get; set; }
    [Parameter] public bool IsDirectory { get; set; } = true;
    [Parameter] public EventCallback OnChanged { get; set; }
    private bool _isLoading, _loaded, _showDetailPerms, _showInheritance;
    private string? _errorMessage;

    protected override async Task OnAfterRenderAsync(bool firstRender) { if (firstRender) await LoadDataAsync(); }
    protected override async Task OnParametersSetAsync() { if (_loaded && (ShareRelativePath.Normalize(Path) != VM.NormalizedPath || ShareId != VM.ShareId)) await LoadDataAsync(); }

    private async Task LoadDataAsync()
    {
        Logger.LogDebug(LogEvents.AclEditorLoadData, LogMessages.AclEditorLoadData, Path, ShareId);
        if (Path is null) { _errorMessage = Resources.Web_Acl_Error_NoPath; Logger.LogDebug(LogEvents.AclEditorNoPath, LogMessages.AclEditorNoPath); StateHasChanged(); return; }
        if (ShareId == Guid.Empty) { _errorMessage = Resources.Web_Acl_Error_NoShareId; Logger.LogDebug(LogEvents.AclEditorNoShareId, LogMessages.AclEditorNoShareId); StateHasChanged(); return; }
        _isLoading = true; _loaded = false; _errorMessage = null; StateHasChanged();
        try
        {
            Logger.LogDebug(LogEvents.AclEditorLoading, LogMessages.AclEditorLoading, Path, ShareId, IsDirectory);
            await VM.LoadAsync(Path, ShareId, IsDirectory);
            Logger.LogDebug(LogEvents.AclEditorLoaded, LogMessages.AclEditorLoaded, VM.IsLoaded, VM.Entries.Count, VM.InheritedEntries.Count);
            if (VM.IsLoaded) _loaded = true;
            else { _errorMessage = VM.ErrorMessage ?? Resources.Web_Acl_Error_LoadFailed; Logger.LogDebug(LogEvents.AclEditorLoadFailed, LogMessages.AclEditorLoadFailed, _errorMessage); }
        }
        catch (Exception ex) { _errorMessage = $"{Resources.Web_Acl_Error_Exception}: {ex.Message}"; Logger.LogError(LogEvents.AclEditorException, ex, LogMessages.AclEditorException); }
        finally { _isLoading = false; StateHasChanged(); }
    }

    private void StartAdd() { _showDetailPerms = false; _showInheritance = false; VM.StartAddEntry(); }
    private void CancelAdd() => VM.CancelAddEntry();
    private void StartEdit(AccessEntry entry) { _showDetailPerms = false; _showInheritance = false; VM.StartEditEntry(entry); }
    private void CancelEdit() => VM.CancelEditEntry();
    private async Task DeleteEntry(Guid id) { await VM.DeleteEntryAsync(id); if (OnChanged.HasDelegate) await OnChanged.InvokeAsync(); }
    private async Task AddEntry() { if (await VM.AddEntryAsync() && OnChanged.HasDelegate) await OnChanged.InvokeAsync(); }
    private async Task SaveEdit() { if (await VM.SaveEditEntryAsync() && OnChanged.HasDelegate) await OnChanged.InvokeAsync(); }
    private void SetPrincipalType(string type) { VM.NewPrincipalType = type; VM.NewPrincipalId = null; }
    private void SetEntryType(AclEntryType type) => VM.NewEntryType = type;
    private void TogglePerm(FilePermission flag) => VM.NewPermissions = VM.TogglePermission(VM.NewPermissions, flag);
    private void ToggleShortcut(FilePermission shortcut) => VM.NewPermissions = VM.ApplyShortcut(VM.NewPermissions, shortcut);
    private void ToggleInheritance(AclInheritance flag) => VM.NewInheritance ^= flag;
    private void OnPrincipalSelected(ChangeEventArgs e) { var value = e.Value?.ToString(); VM.NewPrincipalId = !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var id) ? id : null; }
    private static void AddIf(List<string> parts, bool condition, string text) { if (condition) parts.Add(text); }
    private string FormatPermissions(FilePermission p)
    {
        if (p == FilePermission.FullControl) return Resources.Web_Acl_Permission_FullAccess;
        if (p == FilePermission.ReadAll) return Resources.Web_Acl_Permission_Read;
        if (p == FilePermission.WriteAll) return Resources.Web_Acl_Permission_Write;
        if (p == (FilePermission.ReadAll | FilePermission.WriteAll)) return $"{Resources.Web_Acl_Permission_Read} + {Resources.Web_Acl_Permission_Write}";
        var parts = new List<string>();
        if ((p & FilePermission.ReadAll) == FilePermission.ReadAll) parts.Add(Resources.Web_Acl_Permission_Read); else { AddIf(parts, (p & FilePermission.ListReadData) != 0, Resources.Web_Acl_Perm_ListReadData); AddIf(parts, (p & FilePermission.ReadAttributes) != 0, Resources.Web_Acl_Perm_ReadAttributes); AddIf(parts, (p & FilePermission.TraverseExecute) != 0, Resources.Web_Acl_Perm_TraverseExecute); }
        if ((p & FilePermission.WriteAll) == FilePermission.WriteAll) parts.Add(Resources.Web_Acl_Permission_Write); else { AddIf(parts, (p & FilePermission.CreateWriteData) != 0, Resources.Web_Acl_Perm_CreateWriteData); AddIf(parts, (p & FilePermission.Delete) != 0, Resources.Web_Acl_Perm_Delete); AddIf(parts, (p & FilePermission.DeleteSubItems) != 0, Resources.Web_Acl_Perm_DeleteSubItems); }
        if ((p & FilePermission.AdminAll) == FilePermission.AdminAll) parts.Add(Resources.Web_Acl_Permission_Admin); else { AddIf(parts, (p & FilePermission.ChangePermissions) != 0, Resources.Web_Acl_Perm_ChangePermissions); AddIf(parts, (p & FilePermission.TakeOwnership) != 0, Resources.Web_Acl_Perm_TakeOwnership); }
        return parts.Count > 0 ? string.Join(", ", parts) : Resources.Web_Acl_None;
    }
    private string FormatInheritance(AclInheritance i)
    {
        if (i == AclInheritance.Everything) return Resources.Web_Acl_Inherit_Everything;
        if (i == AclInheritance.ThisOnly) return Resources.Web_Acl_Inherit_ThisOnly;
        if (i == AclInheritance.ThisAndDirect) return Resources.Web_Acl_Inherit_ThisAndDirect;
        var parts = new List<string>(); AddIf(parts, (i & AclInheritance.ThisFolder) != 0, Resources.Web_Acl_Inherit_ThisFolder); AddIf(parts, (i & AclInheritance.SubFolders) != 0, Resources.Web_Acl_Inherit_SubFolders); AddIf(parts, (i & AclInheritance.SubFiles) != 0, Resources.Web_Acl_Inherit_Files); AddIf(parts, (i & AclInheritance.AllDescendants) != 0, Resources.Web_Acl_Inherit_AllDescendants);
        return parts.Count > 0 ? string.Join(", ", parts) : Resources.Web_Acl_None;
    }
}
