using System.Text.Json;

namespace Kaimo_File_Server.Core.Domain.Backup;

/// <summary>Storage backend of a restic repository. Values are persisted; never renumber.</summary>
public enum BackupBackend
{
    Local = 0,
    Sftp = 1,
    Rest = 2,
    S3 = 3,
}

/// <summary>
/// Lifecycle of a repository. A repository only accepts backups once the recovery kit
/// (which holds its encryption password) has been downloaded and confirmed.
/// </summary>
public enum BackupRepositoryState
{
    PendingRecoveryKit = 0,
    Active = 1,
    Disabled = 2,
}

/// <summary>What a job writes into the repository. Only <see cref="Files"/> is offered yet.</summary>
public enum BackupContentLevel
{
    Files = 0,
    FilesAndDatabase = 1,
    Full = 2,
}

public enum BackupSourceKind
{
    Share = 0,
    Pool = 1,
}

public enum BackupRunType
{
    Init = 0,
    Backup = 1,
    Forget = 2,
    Prune = 3,
    Check = 4,
    Copy = 5,
    TestRestore = 6,
    Restore = 7,
}

public enum BackupRunTrigger
{
    Manual = 0,
    Scheduled = 1,
    /// <summary>A missed schedule slot that was caught up after the server was down.</summary>
    CatchUp = 2,
}

public enum BackupRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    SucceededWithWarnings = 3,
    Failed = 4,
    Cancelled = 5,
    /// <summary>The process ended (restart, crash) while the run was queued or running.</summary>
    Interrupted = 6,
}

public enum BackupSnapshotKind
{
    Share = 0,
    Pool = 1,
    System = 2,
    /// <summary>A snapshot Kaimo did not create (e.g. a manual CLI backup into the same repository).</summary>
    Foreign = 3,
}

/// <summary>
/// A restic repository. Global object managed by holders of ManageBackupRepositories and
/// released to departments (inherited by their sub-departments). Secrets are stored only
/// vault-protected; the repository password never leaves the Web process except in the
/// recovery kit.
/// </summary>
public sealed class BackupRepository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public BackupBackend Backend { get; set; }

    /// <summary>Non-secret backend settings (path, endpoint, bucket, …) as JSON.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>Vault-protected backend credentials (REST/S3 keys); null when the backend needs none.</summary>
    public string? EncryptedSecrets { get; set; }

    /// <summary>Vault-protected restic repository password.</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>Repository id from <c>restic cat config</c>; guards against a URL that now points elsewhere.</summary>
    public string? ResticRepositoryId { get; set; }

    public BackupRepositoryState State { get; set; } = BackupRepositoryState.PendingRecoveryKit;

    /// <summary>The server only accepts appends (rest-server <c>--append-only</c>); forget/prune are skipped.</summary>
    public bool IsAppendOnly { get; set; }

    /// <summary>Minimum free space (GB) a local repository target must keep; a backup aborts below it.</summary>
    public int MinFreeSpaceGb { get; set; } = 5;

    public DateTime? KitDownloadedAtUtc { get; set; }
    public DateTime? KitConfirmedAtUtc { get; set; }
    public Guid? KitConfirmedByUserId { get; set; }
    public DateTime? LastReachableAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Release of a repository to a department (and implicitly its sub-departments).</summary>
public sealed class BackupRepositoryDepartment
{
    public Guid RepositoryId { get; set; }
    public Guid DepartmentId { get; set; }
}

/// <summary>Fixed start times on selected weekdays (server local time).</summary>
public sealed class BackupSchedule
{
    public List<TimeOnly> Times { get; set; } = [];
    public List<DayOfWeek> Days { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Times.Count == 0 || Days.Count == 0;

    public static string Serialize(BackupSchedule? value)
        => JsonSerializer.Serialize(value ?? new BackupSchedule());

    public static BackupSchedule Deserialize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new BackupSchedule()
            : JsonSerializer.Deserialize<BackupSchedule>(value) ?? new BackupSchedule();
}

/// <summary>A backup job: which sources go into which repository and when.</summary>
public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public bool Enabled { get; set; } = true;
    public BackupContentLevel ContentLevel { get; set; } = BackupContentLevel.Files;

    /// <summary>Include the share recycle bins (<c>.RECYCLE_BIN</c>).</summary>
    public bool IncludeRecycleBin { get; set; }

    public BackupSchedule Schedule { get; set; } = new();

    /// <summary>The newest schedule slot already handled (persisted before enqueueing).</summary>
    public DateTime? LastScheduledSlotUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public BackupRunStatus? LastStatus { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<BackupJobSource> Sources { get; set; } = [];
}

/// <summary>One source of a job. Separate table so deleting a share cascades.</summary>
public sealed class BackupJobSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public BackupSourceKind Kind { get; set; } = BackupSourceKind.Share;
    public Guid? ShareId { get; set; }
    public string? PoolPath { get; set; }
}

/// <summary>
/// One restic operation (backup, init, …). Doubles as the audit trail of the backup feature.
/// </summary>
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public Guid? JobId { get; set; }
    public BackupRunType Type { get; set; }
    public BackupRunTrigger Trigger { get; set; }
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Queued;
    public Guid? ActorUserId { get; set; }
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>JSON array of the restic snapshot ids this run created.</summary>
    public string? SnapshotIdsJson { get; set; }
    public long FilesNew { get; set; }
    public long FilesChanged { get; set; }
    public long FilesUnmodified { get; set; }
    public long BytesAdded { get; set; }
    public long BytesProcessed { get; set; }
    public int WarningCount { get; set; }

    /// <summary>Mapped, non-secret error code (e.g. <c>wrong_password</c>, <c>source_empty</c>).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Tail of the restic error output (≤ 8 KB), secrets never included.</summary>
    public string? LogExcerpt { get; set; }

    /// <summary>Per-type details, e.g. the per-source outcome of a backup run.</summary>
    public string? DetailsJson { get; set; }
}

/// <summary>
/// Local cache of the repository's snapshot list, so the UI and the scope checks never
/// have to contact the (possibly remote) repository.
/// </summary>
public sealed class BackupSnapshot
{
    public Guid RepositoryId { get; set; }
    public string SnapshotId { get; set; } = string.Empty;
    public DateTime TimeUtc { get; set; }
    public BackupSnapshotKind Kind { get; set; }
    public Guid? JobId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? ShareId { get; set; }
    public string? PoolPath { get; set; }
    public BackupContentLevel ContentLevel { get; set; }

    /// <summary>JSON array of the backed-up paths as recorded in the snapshot.</summary>
    public string PathsJson { get; set; } = "[]";

    /// <summary>JSON array of the snapshot tags.</summary>
    public string TagsJson { get; set; } = "[]";
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
}
