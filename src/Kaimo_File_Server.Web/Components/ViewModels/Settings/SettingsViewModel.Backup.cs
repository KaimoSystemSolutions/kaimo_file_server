using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Backup;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Database-backup schedule/retention settings and manual backup actions.</summary>
public partial class SettingsViewModel
{
    // ── Database backup ──

    /// <summary>Backup schedule/retention working copy edited on the Backup tab.</summary>
    public BackupSettings BackupSettings { get; private set; } = BackupSettings.Default();

    /// <summary>Existing backups on disk, newest first.</summary>
    public IReadOnlyList<BackupFileInfo> Backups { get; private set; } = [];

    /// <summary>True while a manual backup is being created.</summary>
    public bool BackupInProgress { get; private set; }

    /// <summary>Re-reads the list of backups on disk (newest first).</summary>
    public void RefreshBackups() => Backups = _backupService.ListBackups();

    /// <summary>
    /// Builds a single-use capability URL to download a backup; call it on click.
    /// Authorization is enforced here (in the authenticated circuit); the token
    /// embeds the file name and the user, and the controller re-validates both.
    /// Returns "" without permission.
    /// </summary>
    public async Task<string> CreateBackupDownloadUrlAsync(string baseUri, string fileName)
    {
        if (!CanManageBackups)
            return "";
        var actor = await BuildActorContextAsync();
        if (actor is null)
            return "";
        var token = Uri.EscapeDataString(_backupDownloadTokens.Protect(fileName, actor.User.Id));
        return $"{baseUri.TrimEnd('/')}/api/database-backups/download?token={token}";
    }

    /// <summary>Deletes an existing backup file and refreshes the list.</summary>
    public bool DeleteBackup(string fileName)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageBackups)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            if (!_backupService.DeleteBackup(fileName))
            {
                ErrorMessage = R("Web_Settings_Backup_DeleteFailed");
                return false;
            }
            SuccessMessage = R("Web_Settings_Backup_Deleted");
            return true;
        }
        catch (Kaimo_File_Server.Core.Security.ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete backup {FileName}", fileName);
            ErrorMessage = R("Web_Settings_Backup_DeleteFailed");
            return false;
        }
        finally
        {
            RefreshBackups();
        }
    }

    /// <summary>Saves the backup schedule/retention settings.</summary>
    public async Task<bool> SaveBackupAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageBackups)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        BackupSettings.Normalize();

        try
        {
            await _backupSettingsStore.SetAsync(BackupSettings);
            _logger.LogInformation(
                "Backup settings saved (enabled={Enabled}, window={Start}-{End}, keep={Count}/{Days}d).",
                BackupSettings.Enabled, BackupSettings.WindowStart, BackupSettings.WindowEnd,
                BackupSettings.RetentionCount, BackupSettings.RetentionDays);
            SuccessMessage = R("Web_Settings_Backup_Saved");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save backup settings");
            ErrorMessage = R("Web_Settings_Backup_SaveFailed");
            return false;
        }
    }

    /// <summary>Creates a manual backup immediately and refreshes the list.</summary>
    public async Task<bool> CreateBackupNowAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageBackups)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        BackupInProgress = true;
        try
        {
            var created = await _backupService.CreateBackupAsync(BackupTrigger.Manual);
            _logger.LogInformation("Manual database backup created: {FileName}", created.FileName);
            RefreshBackups();
            SuccessMessage = R("Web_Settings_Backup_Created");
            return true;
        }
        catch (Kaimo_File_Server.Core.Security.ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual database backup failed");
            ErrorMessage = R("Web_Settings_Backup_CreateFailed");
            return false;
        }
        finally
        {
            BackupInProgress = false;
        }
    }
}
