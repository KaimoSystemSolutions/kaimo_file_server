using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Moves staged entries into a share. Missing entries are moved as a whole (a directory in
/// one atomic rename); existing directories are merged recursively; existing files follow the
/// conflict policy. Nothing in the destination is ever deleted.
/// </summary>
internal sealed class RestoreMerge(
    string shareRoot,
    Guid shareId,
    BackupRestoreConflict conflict,
    IFileVersionService versions,
    string? userId,
    string renameSuffix,
    BackupRestoreOutcome outcome)
{
    /// <summary>Destination paths that kept their current content (skipped or restored under a new name).</summary>
    public List<string> Untouched { get; } = [];

    public async Task MoveAsync(string source, string relativeDestination)
    {
        var destination = Absolute(relativeDestination);
        var sourceIsDirectory = Directory.Exists(source);

        if (!Path.Exists(destination))
        {
            if (sourceIsDirectory) Directory.Move(source, destination);
            else File.Move(source, destination);
            return;
        }

        if (sourceIsDirectory && Directory.Exists(destination))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(source).ToList())
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(ShareEntryPolicy.InternalNamespacePrefix, StringComparison.Ordinal))
                    continue;
                await MoveAsync(child, ShareRelativePath.Combine(relativeDestination, name));
            }
            return;
        }

        // Same content as in the backup: nothing to restore, no version, no renamed copy.
        if (!sourceIsDirectory && File.Exists(destination) && await SameContentAsync(source, destination))
        {
            outcome.Unchanged++;
            return;
        }

        if (conflict == BackupRestoreConflict.Skip)
        {
            outcome.Skipped++;
            Untouched.Add(relativeDestination);
            return;
        }

        // A folder cannot replace a file (and vice versa) without deleting it: restore beside it.
        if (conflict == BackupRestoreConflict.Rename || sourceIsDirectory || Directory.Exists(destination))
        {
            RestoreBeside(source, relativeDestination, sourceIsDirectory);
            return;
        }

        VersionCaptureResult capture;
        await using (var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            capture = await versions.CaptureVersionAsync(shareId, relativeDestination, current, userId);

        // Overwriting is only safe when the live content stays recoverable from its history;
        // otherwise (policy, full pool) keep it and restore the backup copy beside it.
        if (!capture.PreservesContent)
        {
            RestoreBeside(source, relativeDestination, sourceIsDirectory: false);
            return;
        }

        if (capture.Outcome == VersionCaptureOutcome.Created)
            outcome.VersionsCreated++;
        File.Move(source, destination, overwrite: true);
        outcome.Overwritten++;
    }

    private void RestoreBeside(string source, string relativeDestination, bool sourceIsDirectory)
    {
        var renamed = Absolute(RenamedPath(relativeDestination, !sourceIsDirectory));
        if (sourceIsDirectory) Directory.Move(source, renamed);
        else File.Move(source, renamed);
        outcome.Renamed++;
        Untouched.Add(relativeDestination);
    }

    private static async Task<bool> SameContentAsync(string a, string b)
    {
        await using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (first.Length != second.Length)
            return false;
        var x = new byte[81920];
        var y = new byte[81920];
        int read;
        while ((read = await first.ReadAsync(x)) > 0)
        {
            await second.ReadExactlyAsync(y.AsMemory(0, read));
            if (!x.AsSpan(0, read).SequenceEqual(y.AsSpan(0, read)))
                return false;
        }
        return true;
    }

    /// <summary>"report (restored 2026-10-10).pdf", numbered if that exists too.</summary>
    private string RenamedPath(string relative, bool keepExtension)
    {
        var parent = ShareRelativePath.GetParent(relative);
        var name = ShareRelativePath.GetFileName(relative);
        var extension = keepExtension ? Path.GetExtension(name) : string.Empty;
        var stem = name[..^extension.Length];
        var candidate = ShareRelativePath.Combine(parent, stem + renameSuffix + extension);
        for (var n = 2; Path.Exists(Absolute(candidate)); n++)
            candidate = ShareRelativePath.Combine(parent, $"{stem}{renameSuffix} ({n}){extension}");
        return candidate;
    }

    private string Absolute(string relative)
        => ShareRelativePath.ToContainedAbsolutePath(shareRoot, relative, allowInternalNamespace: false);
}
