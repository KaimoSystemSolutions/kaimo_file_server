using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Startup;

/// <summary>
/// Startup preflight that verifies the service can actually write to the host
/// directories it depends on. The containers run as a non-root user, so a bind
/// mount created with root ownership silently blocks writes. This turns that into
/// one clear, actionable failure at boot instead of a cryptic error much later at
/// the first upload, log write, or database backup.
/// </summary>
public static class WritableDirectoryCheck
{
    /// <summary>
    /// The standard set of writable data directories, derived from the same
    /// configuration keys every entry point already reads. Kept in one place so
    /// Host, Web and the SMB bridge check the identical paths and cannot drift.
    /// Application-data subfolders (<c>.dp-keys</c>, <c>.versions</c>, certificates,
    /// the snapshot cache) live under <c>Storage:ApplicationDataPath</c> and are
    /// covered by probing that root.
    /// </summary>
    /// <param name="includeBackups">
    /// True for services that mount the backup volume (Host, Web); false for the
    /// SMB bridge, which never writes backups.
    /// </param>
    public static IReadOnlyList<(string Label, string Path)> FromConfiguration(
        IConfiguration config, bool includeBackups)
    {
        var storageRoot = config["Storage:RootPath"] ?? "/data/storage";

        var directories = new List<(string Label, string Path)>
        {
            ("ApplicationData", config["Storage:ApplicationDataPath"] ?? "/data/kaimo-system"),
            ("Logs", config["LogArchive:RootPath"] ?? "/data/kaimo-logs"),
        };

        if (includeBackups)
            directories.Add(("Backups", config["Backup:RootPath"] ?? "/data/kaimo-backups"));

        // Each storage pool is its own bind mount with its own ownership, so probe
        // them individually rather than only their (possibly image-layer) parent.
        if (Directory.Exists(storageRoot))
            directories.AddRange(Directory.GetDirectories(storageRoot)
                .Select(path => ($"Storage:{Path.GetFileName(path)}", path)));

        return directories;
    }

    /// <summary>
    /// Convenience overload: verify every directory derived from configuration.
    /// </summary>
    public static void VerifyFromConfiguration(
        ILogger logger, IConfiguration config, bool includeBackups)
        => VerifyAll(logger, FromConfiguration(config, includeBackups));

    /// <summary>
    /// Probes a single directory by ensuring it exists and writing then deleting a
    /// throwaway file. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the directory is not writable.
    /// </summary>
    public static void Probe(string path)
    {
        var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
    }

    /// <summary>
    /// Verifies every (label, path) is writable, logging each result. If any fail,
    /// throws a single aggregated exception naming all of them and the fix, so the
    /// operator sees the full picture from one boot attempt.
    /// </summary>
    public static void VerifyAll(
        ILogger logger, IReadOnlyList<(string Label, string Path)> directories)
    {
        var failures = new List<string>();
        foreach (var (label, path) in directories)
        {
            try
            {
                Probe(path);
                logger.LogInformation("Writable directory OK: {Label} -> {Path}", label, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Directory not writable: {Label} -> {Path}", label, path);
                failures.Add($"{label} ({path})");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "The service account cannot write to the following mounted directories: "
                + string.Join(", ", failures)
                + ". Grant the container user (UID 1654) write access to the corresponding "
                + "host directories, e.g. 'chown -R 1654:1654 <host path>', then restart.");
    }
}
