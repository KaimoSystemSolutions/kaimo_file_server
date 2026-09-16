namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// Creates, lists, restores and prunes PostgreSQL backups using the
/// <c>pg_dump</c> / <c>pg_restore</c> command-line tools. A <c>pg_dump</c> is a
/// read-only, non-destructive operation and may run from any process that has
/// the client installed; <see cref="RestoreAsync"/> is destructive and is only
/// invoked by the database-owning Host process on startup.
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>Absolute path of the folder backups are written to.</summary>
    string BackupRootPath { get; }

    /// <summary>
    /// Creates a compressed custom-format backup and returns its metadata.
    /// Throws if the dump process fails so callers (e.g. the pre-migration hook)
    /// can fail safe.
    /// </summary>
    Task<BackupFileInfo> CreateBackupAsync(BackupTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>Lists existing backups, newest first.</summary>
    IReadOnlyList<BackupFileInfo> ListBackups();

    /// <summary>
    /// Resolves a caller-supplied file name to an absolute path inside the
    /// backup folder, rejecting anything that is not a plain backup file name
    /// (path-traversal safe). Returns <c>null</c> when the file does not exist.
    /// </summary>
    string? ResolveBackupPath(string fileName);

    /// <summary>
    /// Restores the dump at <paramref name="path"/> into the live database
    /// (<c>pg_restore --clean --if-exists</c>). Destructive — Host only.
    /// </summary>
    Task RestoreAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the retention policy, deleting backups that fall outside it. When
    /// <paramref name="pruneScheduled"/> is <c>false</c>, only <c>PreMigration</c> backups
    /// are pruned and scheduled/manual backups are left alone — used when scheduled
    /// backups are disabled, so a user who switched them off does not watch a retention
    /// rule they believed disabled delete their existing scheduled backups. Pre-migration
    /// backups are machine-generated on every upgrade and are the actual unbounded source,
    /// so they are always subject to their own fixed retention.
    /// </summary>
    Task PruneAsync(BackupSettings settings, bool pruneScheduled = true, CancellationToken cancellationToken = default);
}
