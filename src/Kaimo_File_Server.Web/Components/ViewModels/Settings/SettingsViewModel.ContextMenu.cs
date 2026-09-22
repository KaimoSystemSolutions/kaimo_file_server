using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Context-menu matrix editor.
///
/// The editor works in two separate concerns so that toggling membership never
/// reorders anything (the old single-list editor conflated the two, which made
/// rows jump on every click):
///   • membership — which command appears in which category (the matrix), held
///     in _members as "commandId::Scope" keys;
///   • order — one global command order (_order) that every category filters.
/// On save each scope's persisted menu becomes  _order ∩ members(scope), so the
/// renderer keeps consuming ContextMenuConfig.ForScope unchanged.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>The persisted layout (source for load, target for save).</summary>
    public ContextMenuConfig CtxConfig { get; private set; } = ContextMenuConfig.Default();

    /// <summary>All categories = the matrix columns.</summary>
    public static IReadOnlyList<ContextMenuScope> ContextScopes { get; } = Enum.GetValues<ContextMenuScope>();

    /// <summary>The category shown in the live preview (does not affect editing).</summary>
    public ContextMenuScope PreviewScope { get; private set; } = ContextMenuScope.Folder;

    // "commandId::Scope" for every assigned cell.
    private readonly HashSet<string> _members = new();
    // The single global command order (superset of everything shown as matrix rows).
    private List<string> _order = new();

    private static string MKey(string commandId, ContextMenuScope scope) => $"{commandId}::{scope}";

    /// <summary>
    /// Projects the persisted <see cref="CtxConfig"/> into the editor's membership +
    /// order working state. Call after (re)loading or resetting CtxConfig.
    /// </summary>
    public void RebuildContextEditorState()
    {
        _members.Clear();
        foreach (var scope in ContextScopes)
            foreach (var id in CtxConfig.ForScope(scope))
                if (ContextCommandCatalog.ById(id)?.IsValidFor(scope) == true)
                    _members.Add(MKey(id, scope));

        // Start from the stored (or default) order, then reconcile against the catalog
        // so unknown ids are dropped and newly added commands still show up.
        _order = new List<string>(CtxConfig.Order ?? ContextMenuConfig.DefaultOrder);
        _order.RemoveAll(id => ContextCommandCatalog.ById(id) is null);
        foreach (var c in ContextCommandCatalog.All)
            if (!_order.Contains(c.Id))
                _order.Add(c.Id);

        // General items (new folder / refresh) are pinned to the bottom of the menu.
        // OrderBy is a stable sort, so relative order within each group is preserved.
        _order = _order.OrderBy(id => IsGeneralCmd(id) ? 1 : 0).ToList();
    }

    private static bool IsGeneralCmd(string id) => ContextCommandCatalog.GeneralIds.Contains(id);

    /// <summary>Command rows for the matrix, in the global order.</summary>
    public IReadOnlyList<ContextCommand> OrderedCommands =>
        _order.Select(ContextCommandCatalog.ById).Where(c => c is not null).Select(c => c!).ToList();

    /// <summary>True if <paramref name="commandId"/> may appear in <paramref name="scope"/>.</summary>
    public bool IsValidCell(string commandId, ContextMenuScope scope)
        => ContextCommandCatalog.ById(commandId)?.IsValidFor(scope) == true;

    /// <summary>True if the cell is currently ticked.</summary>
    public bool IsCellOn(string commandId, ContextMenuScope scope)
        => _members.Contains(MKey(commandId, scope));

    /// <summary>Toggle a single cell (no-op for invalid combinations).</summary>
    public void ToggleCell(string commandId, ContextMenuScope scope)
    {
        if (!IsValidCell(commandId, scope)) return;
        var key = MKey(commandId, scope);
        if (!_members.Remove(key)) _members.Add(key);
    }

    /// <summary>Toggle a command across every category it is valid for (row action).</summary>
    public void ToggleRow(string commandId)
    {
        var scopes = ContextScopes.Where(s => IsValidCell(commandId, s)).ToList();
        var allOn = scopes.All(s => _members.Contains(MKey(commandId, s)));
        foreach (var s in scopes)
        {
            var key = MKey(commandId, s);
            if (allOn) _members.Remove(key); else _members.Add(key);
        }
    }

    /// <summary>Toggle every valid command for one category (column action).</summary>
    public void ToggleColumn(ContextMenuScope scope)
    {
        var cmds = ContextCommandCatalog.ForScope(scope).Select(c => c.Id).ToList();
        var allOn = cmds.All(id => _members.Contains(MKey(id, scope)));
        foreach (var id in cmds)
        {
            var key = MKey(id, scope);
            if (allOn) _members.Remove(key); else _members.Add(key);
        }
    }

    public void SelectPreviewScope(ContextMenuScope scope) => PreviewScope = scope;

    /// <summary>Move <paramref name="commandId"/> in the global order to sit before
    /// <paramref name="targetId"/> (drag-and-drop reorder).</summary>
    public void ReorderCommand(string commandId, string targetId)
    {
        if (commandId == targetId) return;
        // Keep general and main items in separate blocks: only reorder within a group.
        if (IsGeneralCmd(commandId) != IsGeneralCmd(targetId)) return;
        var from = _order.IndexOf(commandId);
        var to = _order.IndexOf(targetId);
        if (from < 0 || to < 0) return;
        _order.RemoveAt(from);
        if (from < to) to--;
        _order.Insert(to, commandId);
    }

    public void MoveCommandUp(string commandId)
    {
        var i = _order.IndexOf(commandId);
        // Block swapping across the main/general boundary.
        if (i > 0 && IsGeneralCmd(commandId) == IsGeneralCmd(_order[i - 1]))
            (_order[i - 1], _order[i]) = (_order[i], _order[i - 1]);
    }

    public void MoveCommandDown(string commandId)
    {
        var i = _order.IndexOf(commandId);
        if (i >= 0 && i < _order.Count - 1 && IsGeneralCmd(commandId) == IsGeneralCmd(_order[i + 1]))
            (_order[i + 1], _order[i]) = (_order[i], _order[i + 1]);
    }

    /// <summary>The commands that would render for <see cref="PreviewScope"/>, in order.</summary>
    public IReadOnlyList<ContextCommand> PreviewCommands =>
        _order.Where(id => _members.Contains(MKey(id, PreviewScope)) && IsValidCell(id, PreviewScope))
              .Select(ContextCommandCatalog.ById).Where(c => c is not null).Select(c => c!).ToList();

    public void ResetContextToDefault()
    {
        CtxConfig = ContextMenuConfig.Default();
        RebuildContextEditorState();
    }

    public async Task<bool> SaveContextMenuAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageSettings)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        // Fold the editor working state back into the persisted, per-scope format.
        CtxConfig.Order = new List<string>(_order);
        foreach (var scope in ContextScopes)
        {
            var ids = _order
                .Where(id => _members.Contains(MKey(id, scope)) && IsValidCell(id, scope))
                .ToList();
            CtxConfig.Menus[scope.ToString()] = ids;
        }

        try
        {
            await _config.SetAsync(ContextMenuConfig.ConfigKey, CtxConfig);
            _logger.LogInformation("Context menu layout saved");
            SuccessMessage = Resources.Web_Settings_CtxSaved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save context menu layout");
            ErrorMessage = Resources.Web_Settings_CtxSaveFailed;
            return false;
        }
    }
}
