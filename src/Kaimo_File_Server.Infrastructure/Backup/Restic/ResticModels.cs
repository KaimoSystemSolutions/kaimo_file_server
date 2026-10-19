namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Progress of a running backup (from restic's <c>status</c> messages).</summary>
public sealed record ResticProgress(double PercentDone, long FilesDone, long TotalFiles, long BytesDone, long TotalBytes);
/// <summary>Outcome of <c>restic backup</c>.</summary>
public sealed record ResticBackupResult(
    string? SnapshotId,
    long FilesNew,
    long FilesChanged,
    long FilesUnmodified,
    long BytesAdded,
    long BytesProcessed,
    int WarningCount,
    IReadOnlyList<string> Warnings,
    string StderrTail)
{
    /// <summary>Exit code 3: snapshot written, but some files could not be read.</summary>
    public bool HasWarnings => WarningCount > 0;
}
/// <summary>One entry of <c>restic snapshots --json</c>.</summary>
public sealed record ResticSnapshotInfo(
    string Id,
    DateTime TimeUtc,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags,
    string? Hostname,
    long FileCount,
    long TotalBytes);
/// <summary>One entry of <c>restic ls --json</c>; <see cref="Path"/> is absolute inside the snapshot.</summary>
public sealed record ResticNode(string Name, string Path, bool IsDirectory, long Size, DateTime? ModifiedUtc);
/// <summary>Outcome of <c>restic restore</c>.</summary>
public sealed record ResticRestoreResult(long FilesRestored, long BytesRestored);
/// <summary>Outcome of <c>restic check</c>: <see cref="Ok"/> is false when restic found damage.</summary>
public sealed record ResticCheckResult(bool Ok, IReadOnlyList<string> Errors, string OutputTail);
/// <summary>What a backup should contain.</summary>
public sealed record ResticBackupRequest(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Excludes);
