using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Language;
using Microsoft.AspNetCore.Components.Web;

namespace Kaimo_File_Server.Web.Components.Pages.Departments;

public partial class Departments
{
    private bool membersOpen, groupsOpen, sharesOpen;
    private string CreateParentIdString { get => VM.CreateParentId?.ToString() ?? ""; set => VM.CreateParentId = Guid.TryParse(value, out var id) ? id : null; }
    private string EditParentIdString { get => VM.EditParentId?.ToString() ?? ""; set => VM.EditParentId = Guid.TryParse(value, out var id) ? id : null; }
    protected override async Task OnAfterRenderAsync(bool firstRender) { if (firstRender) { await VM.LoadAsync(); StateHasChanged(); } }
    private async Task Select(Department dept) { CloseDropdowns(); await VM.SelectAsync(dept); StateHasChanged(); }
    private async Task Edit() { CloseDropdowns(); await VM.StartEditAsync(); StateHasChanged(); }
    private async Task Save() { CloseDropdowns(); await VM.SaveAsync(); StateHasChanged(); }
    private async Task Create() { await VM.CreateAsync(); StateHasChanged(); }
    private async Task ConfirmDelete() { await VM.ConfirmDeleteAsync(); StateHasChanged(); }
    private void CancelEdit() { CloseDropdowns(); VM.CancelEdit(); StateHasChanged(); }
    private void ToggleCollapse(Guid id) { VM.ToggleCollapse(id); StateHasChanged(); }
    private async Task HandleCreateKeyDown(KeyboardEventArgs e) { if (e.Key == "Enter") await Create(); }
    private void CloseDropdowns() { membersOpen = false; groupsOpen = false; sharesOpen = false; }
    private static string Checked(IEnumerable<string> names, string fallback) { var list = names.ToList(); return list.Count > 0 ? string.Join(", ", list) : fallback; }
    private static string Format(IEnumerable<string> items, int max) { var list = items.ToList(); if (list.Count == 0) return ""; var visible = string.Join(", ", list.Take(max)); if (list.Count > max) visible += " " + string.Format(Resources.Web_Common_MoreItems, list.Count - max); return visible; }
}
