using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Undo for the open folder: every successful operation records its inverse, and
/// <see cref="UndoAsync"/> replays the inverses of the last entry. The history lives in
/// this circuit-scoped view model only, so a browser reload drops it; navigating to another
/// folder and the explicit refresh clear it too. Undo never deletes permanently: anything
/// it cannot revert through a rename or the recycle bin is not recorded, and drops the
/// whole history, since older inverses would then act on changed items.
/// </summary>
public partial class FileBrowserViewModel
{
    private const int MaxUndoEntries = 20;

    // Inverse step: RestoreTo null = recycle Path; otherwise rename Path back to RestoreTo.
    private sealed record UndoStep(string Path, string? RestoreTo);

    private readonly List<List<UndoStep>> _undoStack = [];
    private List<UndoStep>? _undoGroup;
    private bool _undoGroupInvalid;
    // Scopes of the open group not yet disposed: nested calls and batches that overlap.
    private int _undoGroupScopes;
    private string? _undoLocation;
    // Changes on every folder change, so a batch pinned to an earlier visit of the same
    // folder is told apart from the current one.
    private int _undoVisit;

    /// <summary>False for browsers that must not offer undo (anonymous public share links).</summary>
    protected virtual bool SupportsUndo => true;

    // A running batch blocks undo: its entry is still being recorded.
    public bool CanUndo => SupportsUndo && _undoStack.Count > 0 && _undoGroup is null;

    /// <summary>
    /// Drops the history, including a batch still being recorded (it may run on after the
    /// user navigated to another folder or share, where its paths would not belong).
    /// </summary>
    public void ClearUndo()
    {
        _undoStack.Clear();
        _undoGroupInvalid = true;
    }

    /// <summary>
    /// Collects every operation until the last scope is disposed into a single undo entry
    /// (multi-delete, drag of several items, paste). The entry is dropped when any of its
    /// operations cannot be reverted or the history is cleared meanwhile. Batches no longer
    /// block the circuit: nested calls, overlapping batches and anything else done while one
    /// runs all join the same entry and are undone together.
    /// </summary>
    public IDisposable? BeginUndoGroup()
    {
        if (_undoGroup is null)
        {
            _undoGroup = [];
            _undoGroupInvalid = false;
        }
        _undoGroupScopes++;
        return new UndoGroupScope(this);
    }

    public async Task<OperationResult> UndoAsync()
    {
        if (!CanUndo)
            return OperationResult.Fail(Resources.Web_Undo_Failed);

        var steps = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        IFileService service;
        UserContext user;
        try
        {
            (service, user) = await RequireServiceAndUserAsync();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Undo failed");
            return OperationResult.Fail(Resources.Web_Undo_Failed);
        }

        // One failing step never stops the others, so a batch is restored as far as possible.
        var failed = 0;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            try
            {
                // Always through the recycle bin: undo must stay recoverable itself.
                if (step.RestoreTo is null)
                    await service.DeleteFileAsync(step.Path, user, isRecycleEnabled: true);
                else
                    await service.RenameAsync(step.Path, step.RestoreTo, user);
            }
            catch (Exception ex)
            {
                // Typical causes: the original name was reused meanwhile, its parent folder is
                // gone, or the recycle bin entry was purged. The entry is dropped either way.
                _logger.LogWarning(ex, "Undo step failed for '{Path}'", step.Path);
                failed++;
            }
        }

        if (failed == 0)
            return OperationResult.Ok();
        return OperationResult.Fail(failed == steps.Count
            ? Resources.Web_Undo_Failed
            : string.Format(Resources.Web_Undo_Partial, failed, steps.Count));
    }

    // Called on every load: the history only spans the folder it was recorded in.
    private void TrackUndoLocation(string shareName, string? subPath)
    {
        var location = shareName + "\n" + (subPath ?? "").Trim('/');
        if (location == _undoLocation)
            return;
        _undoLocation = location;
        _undoVisit++;
        ClearUndo();
    }

    // A pinned batch that outlived a navigation must not touch the new visit's history,
    // even when the user came back to the same folder.
    private bool IsLeftLocation(LocalFileOperationTarget? target)
        => target is not null && target.UndoVisit != _undoVisit;

    private void RecordRenamed(string from, string to, LocalFileOperationTarget? target)
    {
        if (!IsLeftLocation(target))
            RecordUndo(new UndoStep(to, from));
    }

    private void RecordRecycled(string originalPath, string? recyclePath, LocalFileOperationTarget? target)
    {
        if (IsLeftLocation(target))
            return;
        if (recyclePath is null)
            ClearUndo(); // deleted permanently
        else
            RecordUndo(new UndoStep(recyclePath, originalPath));
    }

    private void RecordCreated(string path, bool overwroteExisting = false, LocalFileOperationTarget? target = null)
    {
        if (IsLeftLocation(target))
            return;
        // The inverse of a create is a recycle; without a recycle bin it would delete
        // permanently, and an overwritten file cannot be brought back at all.
        if (overwroteExisting || (target?.Share ?? CurrentShare)?.IsRecycleEnabled != true)
        {
            ClearUndo();
            return;
        }
        // Already covered when this group created one of its parent folders.
        if (_undoGroup?.Any(s => s.RestoreTo is null && path.StartsWith(s.Path + "/", StringComparison.Ordinal)) == true)
            return;
        RecordUndo(new UndoStep(path, null));
    }

    private void RecordUndo(UndoStep step)
    {
        if (!SupportsUndo)
            return;
        if (_undoGroup is not null)
            _undoGroup.Add(step);
        else
            PushUndo([step]);
    }

    private void PushUndo(List<UndoStep> steps)
    {
        _undoStack.Add(steps);
        if (_undoStack.Count > MaxUndoEntries)
            _undoStack.RemoveAt(0);
    }

    private static bool ExistsOnDisk(IFileService service, string path)
    {
        var absolute = service.ToAbsolutePath(path);
        return File.Exists(absolute) || Directory.Exists(absolute);
    }

    private sealed class UndoGroupScope(FileBrowserViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (--owner._undoGroupScopes > 0)
                return;
            if (owner._undoGroup is { Count: > 0 } steps && !owner._undoGroupInvalid)
                owner.PushUndo(steps);
            owner._undoGroup = null;
        }
    }
}
