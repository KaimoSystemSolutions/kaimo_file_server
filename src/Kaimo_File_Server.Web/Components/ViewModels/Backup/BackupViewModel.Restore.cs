using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Infrastructure.Backup.Restic;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

public partial class BackupViewModel
{
    // ══════════════════════════════════════════
    //  Restore
    // ══════════════════════════════════════════

    public IReadOnlyList<BackupRestoreShare> RestoreShares { get; private set; } = [];

    /// <summary>System, pool and foreign backup points (Global repository admins only).</summary>
    public IReadOnlyList<BackupServerPointView> ServerPoints { get; private set; } = [];

    /// <summary>The selected group of <see cref="ServerPoints"/> (by <see cref="BackupServerPointView.Source"/>).</summary>
    public string? ServerSource { get; private set; }

    /// <summary>Folder below which system and pool backup points are restored.</summary>
    public string ServerRestoreRoot => restoreExecutor.ServerRestoreRoot;

    public void SelectServerSource(string source)
    {
        ClearMessages();
        RestoreShare = null;
        ServerSource = source;
    }

    public Task<bool> RestoreServerPointAsync(BackupServerPointView point) => RunAsync(async actor =>
    {
        await catalog.EnqueueServerRestoreAsync(actor, point.RepositoryId, point.SnapshotId);
        Runs = await catalog.ListRunsAsync(actor);
        SuccessMessage = R("Web_Backup_Restore_ServerStarted");
    });
    public BackupRestoreShare? RestoreShare { get; private set; }
    public IReadOnlyList<BackupPointView> BackupPoints { get; private set; } = [];
    public BackupPointView? BackupPoint { get; private set; }

    /// <summary>Share-relative folder shown in the backup-point browser ("" = share root).</summary>
    public string BrowseFolder { get; private set; } = string.Empty;
    public IReadOnlyList<BackupBrowseEntry> BrowseEntries { get; private set; } = [];
    public bool IsBrowsing { get; private set; }
    public HashSet<string> SelectedEntries { get; } = new(StringComparer.Ordinal);
    public (bool FromBackup, bool InPlace) RestoreRights { get; private set; }
    public IReadOnlyList<ShareDefinition> RestoreTargets { get; private set; } = [];
    public BackupRestoreForm RestoreForm { get; private set; } = new();

    /// <summary>Key of a backup point in the picker (snapshot ids are only unique per repository).</summary>
    public static string PointKey(BackupPointView point) => point.RepositoryId.ToString("N") + ":" + point.SnapshotId;

    /// <summary>Nothing or everything selected: the restore takes the whole folder.</summary>
    public bool RestoresWholeFolder => SelectedEntries.Count == 0 || SelectedEntries.Count == BrowseEntries.Count;

    public Task<bool> SelectRestoreShareAsync(Guid shareId) => RunAsync(async actor =>
    {
        RestoreShare = RestoreShares.FirstOrDefault(s => s.ShareId == shareId);
        ServerSource = null;
        BackupPoints = await catalog.ListBackupPointsAsync(actor, shareId);
        RestoreRights = await catalog.GetRestoreRightsAsync(actor, shareId);
        BackupPoint = BackupPoints.FirstOrDefault();
        await BrowseCoreAsync(actor, string.Empty);
    });

    public Task<bool> SelectBackupPointAsync(string key) => RunAsync(async actor =>
    {
        BackupPoint = BackupPoints.FirstOrDefault(p => PointKey(p) == key);
        // The same folder usually exists in the neighbouring backup point; fall back to the root.
        try { await BrowseCoreAsync(actor, BrowseFolder); }
        catch (ResticException) { await BrowseCoreAsync(actor, string.Empty); }
    });

    public Task<bool> OpenFolderAsync(string folder) => RunAsync(actor => BrowseCoreAsync(actor, folder));

    private async Task BrowseCoreAsync(UserContext actor, string folder)
    {
        SelectedEntries.Clear();
        BrowseEntries = [];
        BrowseFolder = folder;
        if (BackupPoint is null)
            return;
        IsBrowsing = true;
        Changed?.Invoke();
        try
        {
            BrowseEntries = await catalog.BrowseAsync(actor, BackupPoint.RepositoryId, BackupPoint.SnapshotId, folder);
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    public void ToggleEntry(string name)
    {
        if (!SelectedEntries.Remove(name))
            SelectedEntries.Add(name);
    }

    public void SelectAllEntries(bool selected)
    {
        SelectedEntries.Clear();
        if (selected)
            SelectedEntries.UnionWith(BrowseEntries.Select(e => e.Name));
    }

    /// <summary>Resets the dialog to the least invasive mode the actor may use.</summary>
    public Task<bool> PrepareRestoreAsync() => RunAsync(async actor =>
    {
        // "Another share" never offers the share being restored (that is "new folder").
        RestoreTargets = (await catalog.ListRestoreTargetSharesAsync(actor)).Where(s => s.Id != RestoreShare?.ShareId).ToList();
        RestoreForm = new BackupRestoreForm
        {
            Mode = RestoreRights.FromBackup ? BackupRestoreMode.NewFolder : BackupRestoreMode.InPlace,
            TargetShareId = RestoreTargets.FirstOrDefault()?.Id ?? Guid.Empty,
        };
    });

    /// <summary>RestoreBackupInPlace on the destination is needed to write permissions back.</summary>
    public bool CanRestorePermissions => RestoreForm.Mode == BackupRestoreMode.OtherShare
        ? RestoreForm.TargetShareId != Guid.Empty
        : RestoreRights.InPlace;

    public Task<bool> StartRestoreAsync() => RunAsync(async actor =>
    {
        if (RestoreShare is null || BackupPoint is null)
            return;
        var form = RestoreForm;
        await catalog.EnqueueRestoreAsync(actor, new BackupRestoreRequest(
            BackupPoint.RepositoryId,
            BackupPoint.SnapshotId,
            RestoreShare.ShareId,
            BrowseFolder,
            RestoresWholeFolder ? [] : SelectedEntries.ToList(),
            form.Mode,
            form.Conflict,
            form.RestorePermissions && CanRestorePermissions,
            form.Mode == BackupRestoreMode.OtherShare ? form.TargetShareId : null,
            form.Mode == BackupRestoreMode.OtherShare ? form.TargetFolder : null));
        Runs = await catalog.ListRunsAsync(actor);
        SuccessMessage = R("Web_Backup_Restore_Started");
    });

    /// <summary>Single-use URL streaming one entry of the current folder (file raw, folder as ZIP).</summary>
    public string? CreateEntryDownloadUrl(string baseUri, string name)
    {
        if (_actor is null || demo.ReadOnly || !RestoreRights.FromBackup || BackupPoint is null)
            return null;
        var subject = Controllers.ResticDownloadController.DownloadSubject(
            BackupPoint.RepositoryId, BackupPoint.SnapshotId, Kaimo_File_Server.Core.Helpers.ShareRelativePath.Combine(BrowseFolder, name));
        return $"{baseUri.TrimEnd('/')}/api/file-backups/download?token={Uri.EscapeDataString(downloadTokens.Protect(subject, _actor.User.Id))}";
    }
}
