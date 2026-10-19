using Kaimo_File_Server.Core.Domain.Backup;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>What the current actor may do on the backup page.</summary>
public sealed record BackupAccess(
    bool CanManageRepositories,
    bool CanManageJobs,
    bool JobsUnrestricted,
    IReadOnlySet<Guid> JobShareIds,
    bool RestoreUnrestricted = false,
    IReadOnlySet<Guid>? RestoreShareIds = null)
{
    /// <summary>Holds RestoreFromBackup or RestoreBackupInPlace somewhere.</summary>
    public bool CanRestore => RestoreUnrestricted || RestoreShareIds is { Count: > 0 };
    public bool CanAccessPage => CanManageRepositories || CanManageJobs || CanRestore;
    public static readonly BackupAccess None = new(false, false, false, new HashSet<Guid>());
}

/// <summary>A share with restorable backup points.</summary>
public sealed record BackupRestoreShare(Guid ShareId, string Name, int BackupPointCount, DateTime LatestUtc);

/// <summary>One backup point (restic snapshot) of a share, from the local cache.</summary>
public sealed record BackupPointView(
    Guid RepositoryId, string RepositoryName, string SnapshotId, DateTime TimeUtc, string? JobName, long FileCount, long TotalBytes);

/// <summary>A system, pool or foreign backup point; restored as a whole into the server's backup folder (Global).</summary>
public sealed record BackupServerPointView(
    Guid RepositoryId, string RepositoryName, string SnapshotId, DateTime TimeUtc, BackupSnapshotKind Kind,
    string Source, BackupContentLevel ContentLevel, string? JobName, long FileCount, long TotalBytes);

/// <summary>An entry of a folder inside a backup point.</summary>
public sealed record BackupBrowseEntry(string Name, bool IsDirectory, long Size, DateTime? ModifiedUtc);

/// <summary>A single file or folder of a backup point, ready to be streamed (file raw, folder as ZIP).</summary>
public sealed record BackupDownload(string FileName, bool IsDirectory, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>Input of the repository form. Secrets are optional on update (null/empty = keep).</summary>
public sealed record BackupRepositoryDraft(
    string Name,
    BackupBackend Backend,
    ResticRepositorySettings Settings,
    ResticBackendSecrets Secrets,
    bool IsAppendOnly,
    int MinFreeSpaceGb,
    IReadOnlyList<Guid> DepartmentIds,
    BackupSchedule? MaintenanceSchedule = null,
    bool ReadDataMonthly = true,
    int TestRestoreSampleSize = 20,
    Guid? MirrorTargetId = null);

/// <summary>Input of the job form. Pools, database and Full levels are Global-only.</summary>
public sealed record BackupJobDraft(
    Guid? Id,
    string Name,
    Guid RepositoryId,
    bool Enabled,
    bool IncludeRecycleBin,
    IReadOnlyList<Guid> ShareIds,
    IReadOnlyList<TimeOnly> Times,
    IReadOnlyList<DayOfWeek> Days,
    BackupRetention? Retention = null,
    int AlertAfterDays = 3,
    BackupContentLevel ContentLevel = BackupContentLevel.Files,
    IReadOnlyList<string>? PoolPaths = null,
    bool IncludeVersions = false);

/// <summary>Result of probing a repository location before creating it.</summary>
public enum BackupRepositoryProbe
{
    /// <summary>Reachable, no repository yet: can be initialized.</summary>
    Empty,

    /// <summary>A restic repository exists there: connect it with its password.</summary>
    Existing,
}

/// <summary>A repository row plus its department releases and job count (Global view).</summary>
public sealed record BackupRepositoryView(BackupRepository Repository, IReadOnlyList<Guid> DepartmentIds, int JobCount);

/// <summary>A repository as offered in the job form (no settings or secrets).</summary>
public sealed record BackupRepositoryChoice(Guid Id, string Name, BackupBackend Backend);

/// <summary>Active repositories plus, per selectable share, which of them the actor may use for it.</summary>
public sealed record BackupRepositoryChoices(
    IReadOnlyList<BackupRepositoryChoice> Repositories,
    IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> UsableByShare);

/// <summary>A job with the display names of its sources.</summary>
public sealed record BackupJobView(BackupJob Job, string RepositoryName, IReadOnlyList<(Guid ShareId, string Name)> Shares);
