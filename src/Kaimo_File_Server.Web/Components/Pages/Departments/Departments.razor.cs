using Kaimo_File_Server.Core.Domain.Department;
using Microsoft.AspNetCore.Components.Web;

namespace Kaimo_File_Server.Web.Components.Pages.Departments;

public partial class Departments
{
    private DepartmentDetailTab _activeTab = DepartmentDetailTab.General;
    protected override async Task OnAfterRenderAsync(bool firstRender) { if (firstRender) { await VM.LoadAsync(); StateHasChanged(); } }
    private async Task Select(Department dept) { _activeTab = DepartmentDetailTab.General; await VM.SelectAsync(dept); StateHasChanged(); }
    private async Task Edit() { await VM.StartEditAsync(); StateHasChanged(); }
    private async Task Save() { await VM.SaveAsync(); StateHasChanged(); }
    private async Task Create() { await VM.CreateAsync(); StateHasChanged(); }
    private async Task ConfirmDelete() { await VM.ConfirmDeleteAsync(); StateHasChanged(); }
    private void CancelEdit() { VM.CancelEdit(); StateHasChanged(); }
    private void ToggleCollapse(Guid id) { VM.ToggleCollapse(id); StateHasChanged(); }
    private async Task HandleCreateKeyDown(KeyboardEventArgs e) { if (e.Key == "Enter") await Create(); }
    private void SwitchTab(DepartmentDetailTab tab) { _activeTab = tab; }
    private static string R(string key) => Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString(key) ?? key;
    private async Task StartAddAssignment() { await VM.StartAddAssignmentAsync(); StateHasChanged(); }
    private void CancelAddAssignment() { VM.CancelAddAssignment(); StateHasChanged(); }
    private async Task CreateAssignment() { await VM.CreateAssignmentAsync(); StateHasChanged(); }
    private async Task DeleteAssignment(Guid id) { await VM.DeleteAssignmentAsync(id); StateHasChanged(); }
}

internal enum DepartmentDetailTab
{
    General,
    Permissions,
    Assignments
}
