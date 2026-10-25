using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Items of one area split by health, for the distribution bars on the start page.</summary>
public sealed record HealthCounts(int Ok, int Warning, int Failed, int Inactive)
{
    public static readonly HealthCounts Empty = new(0, 0, 0, 0);
}

/// <summary>One active client device of the signed-in user and its enabled sync folders.</summary>
public sealed record HomeDeviceItem(SyncDevice Device, int SyncFolderCount);

/// <summary>
/// Start page: a read-only overview for every user (home folder, client
/// devices) plus, per management permission, the state of the areas the user runs
/// (external-storage syncs and connections, share links, backups, users, security).
///
/// Each section reuses the gate and data source of the page it links to, so the start
/// page never shows more than that page would. A section that fails to load is logged
/// and left out instead of breaking the whole page.
/// </summary>
public sealed class HomeDashboardViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContexts,
    IManagementAuthService mgmtAuth,
    ShareBrowserViewModel shareBrowser,
    ExternalStorageSyncViewModel syncs,
    ShareLinkListViewModel shareLinks,
    ISyncDeviceRepository devices,
    IDeviceSyncProfileRepository deviceProfiles,
    IStorageConnectionRepository connections,
    IUserRepository users,
    BackupCatalogService backups,
    SecurityMonitor securityMonitor,
    ISecurityEventRepository securityEvents,
    ILogger<HomeDashboardViewModel> logger)
{
    public bool IsLoading { get; private set; } = true;
    public string DisplayName { get; private set; } = "";

    // ── Every user ──
    public bool HasHome { get; private set; }
    public IReadOnlyList<HomeDeviceItem> Devices { get; private set; } = [];

    // ── External storage (SyncAdmin / global connection rights) ──
    public bool CanSeeSyncs { get; private set; }
    public IReadOnlyList<ExternalStorageSyncListItem> Syncs { get; private set; } = [];
    public IReadOnlyList<ExternalStorageSyncListItem> SyncIssues { get; private set; } = [];
    public HealthCounts SyncHealth { get; private set; } = HealthCounts.Empty;
    public bool CanSeeConnections { get; private set; }
    public IReadOnlyList<StorageConnection> Connections { get; private set; } = [];
    public IReadOnlyList<StorageConnection> ConnectionIssues { get; private set; } = [];
    public HealthCounts ConnectionHealth { get; private set; } = HealthCounts.Empty;

    // ── Share links (ManageShareLinks / ManageUploadLinks) ──
    public bool CanSeeShareLinks { get; private set; }
    public int ActiveDownloadLinks { get; private set; }
    public int ActiveUploadLinks { get; private set; }

    // ── Backups (job administrators) ──
    public bool CanSeeBackups { get; private set; }
    public IReadOnlyList<BackupJobView> BackupJobs { get; private set; } = [];
    public IReadOnlyList<BackupJobView> BackupIssues { get; private set; } = [];
    public HealthCounts BackupHealth { get; private set; } = HealthCounts.Empty;

    // ── Users and security ──
    public bool CanSeeUsers { get; private set; }
    public int UserCount { get; private set; }
    public int DisabledUserCount { get; private set; }
    public bool CanSeeSecurity { get; private set; }
    public SecurityTotals? SecurityLast24h { get; private set; }

    public bool HasAdminSections
        => CanSeeSyncs || CanSeeConnections || CanSeeShareLinks || CanSeeBackups || CanSeeUsers || CanSeeSecurity;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var state = await authState.GetAuthenticationStateAsync();
            DisplayName = state.User.FindFirst("display_name")?.Value ?? state.User.Identity?.Name ?? "";
            var username = state.User.Identity?.Name;
            var actor = string.IsNullOrEmpty(username) ? null : await userContexts.CreateByUsernameAsync(username);
            if (actor is null)
                return;

            await SectionAsync("shares", async () =>
            {
                await shareBrowser.LoadAsync(loadSizes: false);
                HasHome = shareBrowser.HasHome;
            });

            await SectionAsync("devices", async () =>
            {
                var profiles = (await deviceProfiles.GetByUserAsync(actor.User.Id))
                    .Where(p => p.Enabled)
                    .GroupBy(p => p.DeviceId)
                    .ToDictionary(g => g.Key, g => g.Count());
                Devices = (await devices.GetByUserAsync(actor.User.Id))
                    .Where(d => d.IsActive)
                    .OrderByDescending(d => d.LastSeenUtc)
                    .Select(d => new HomeDeviceItem(d, profiles.GetValueOrDefault(d.Id)))
                    .ToList();
            });

            // Same gate as ExternalStorageSyncViewModel / the "External Storage" nav entry.
            CanSeeSyncs = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.SyncAdmin);
            if (CanSeeSyncs)
                await SectionAsync("syncs", async () =>
                {
                    await syncs.LoadAsync();
                    Syncs = syncs.Syncs;
                    SyncIssues = Syncs.Where(s => s.Definition.Enabled && HasSyncIssue(s)).ToList();
                    var enabled = Syncs.Where(s => s.Definition.Enabled).ToList();
                    // A hard abort (error code) counts as failed, per-item failures as a warning.
                    var failed = enabled.Count(s => !string.IsNullOrWhiteSpace(s.Runtime?.LastErrorCode));
                    SyncHealth = new HealthCounts(
                        Ok: enabled.Count - SyncIssues.Count,
                        Warning: SyncIssues.Count - failed,
                        Failed: failed,
                        Inactive: Syncs.Count - enabled.Count);
                });

            // Connections are global (CloudAccessViewModel): manage or use at global scope.
            CanSeeConnections = await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.ManageConnections)
                                || await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.UseConnections);
            if (CanSeeConnections)
                await SectionAsync("connections", async () =>
                {
                    Connections = await connections.GetAllAsync();
                    ConnectionIssues = Connections
                        .Where(c => c.State is not (StorageConnectionState.Ready or StorageConnectionState.Disabled))
                        .ToList();
                    var disabled = Connections.Count(c => c.State == StorageConnectionState.Disabled);
                    ConnectionHealth = new HealthCounts(
                        Ok: Connections.Count - disabled - ConnectionIssues.Count,
                        Warning: ConnectionIssues.Count,
                        Failed: 0,
                        Inactive: disabled);
                });

            CanSeeShareLinks = await mgmtAuth.HasAnyPermissionAsync(
                actor, ManagementPermission.ManageShareLinks | ManagementPermission.ManageUploadLinks);
            if (CanSeeShareLinks)
                await SectionAsync("share links", async () =>
                {
                    await shareLinks.LoadAsync();
                    var active = shareLinks.Links.Where(r => r.StatusKey == "active").ToList();
                    ActiveDownloadLinks = active.Count(r => r.Link.Kind == ShareLinkKind.Download);
                    ActiveUploadLinks = active.Count(r => r.Link.Kind == ShareLinkKind.Upload);
                });

            CanSeeBackups = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ManageBackupJobs);
            if (CanSeeBackups)
                await SectionAsync("backups", async () =>
                {
                    BackupJobs = await backups.ListJobsAsync(actor);
                    var now = DateTime.UtcNow;
                    var states = BackupJobs.Select(j => (Job: j, State: BackupState(j.Job, now))).ToList();
                    BackupIssues = states.Where(x => x.State is HealthState.Warning or HealthState.Failed)
                        .Select(x => x.Job).ToList();
                    BackupHealth = new HealthCounts(
                        Ok: states.Count(x => x.State == HealthState.Ok),
                        Warning: states.Count(x => x.State == HealthState.Warning),
                        Failed: states.Count(x => x.State == HealthState.Failed),
                        Inactive: states.Count(x => x.State == HealthState.Inactive));
                });

            // Counts cover every account, so only global user administrators see them.
            CanSeeUsers = await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.EditUserProfiles);
            if (CanSeeUsers)
                await SectionAsync("users", async () =>
                {
                    var all = (await users.GetAllAsync()).ToList();
                    UserCount = all.Count;
                    DisabledUserCount = all.Count(u => !u.IsEnabled);
                });

            CanSeeSecurity = await mgmtAuth.HasAnyPermissionAsync(actor, ManagementPermission.ViewSecurityMonitor);
            if (CanSeeSecurity)
                await SectionAsync("security", async () =>
                {
                    // Include what was recorded since the last background flush (as the security page does).
                    await securityMonitor.FlushAsync(securityEvents);
                    var now = DateTime.UtcNow;
                    SecurityLast24h = await securityEvents.GetTotalsAsync(now.AddHours(-24), now);
                });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Mirrors the warning tone of the sync list (SyncAdministration.SyncStateTone).</summary>
    public static bool HasSyncIssue(ExternalStorageSyncListItem item)
        => !string.IsNullOrWhiteSpace(item.Runtime?.LastErrorCode) || item.Runtime?.LastRunFailureCount > 0;

    private enum HealthState { Ok, Warning, Failed, Inactive }

    // Same reading as the backup job list: failed run → failed; overdue or warnings → warning;
    // disabled or never run → inactive.
    private static HealthState BackupState(Core.Domain.Backup.BackupJob job, DateTime nowUtc)
        => !job.Enabled || job.LastStatus is null ? HealthState.Inactive
            : job.LastStatus is Core.Domain.Backup.BackupRunStatus.Failed
                or Core.Domain.Backup.BackupRunStatus.Interrupted ? HealthState.Failed
            : BackupSchedulerService.IsStale(job, nowUtc)
              || job.LastStatus == Core.Domain.Backup.BackupRunStatus.SucceededWithWarnings ? HealthState.Warning
            : HealthState.Ok;

    private async Task SectionAsync(string name, Func<Task> load)
    {
        try
        {
            await load();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Start page: failed to load the {Section} section", name);
        }
    }
}
