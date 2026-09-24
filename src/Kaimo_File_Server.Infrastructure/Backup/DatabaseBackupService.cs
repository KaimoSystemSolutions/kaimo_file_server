using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// PostgreSQL backup implementation on top of <c>pg_dump</c> / <c>pg_restore</c>.
/// The password is passed to the child process through <c>PGPASSWORD</c> rather
/// than on the command line so it never appears in the process listing.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    // Pre-migration backups are safety nets around schema changes; keep them far
    // longer than routine scheduled backups and never prune them by count.
    private const int PreMigrationRetentionDays = 90;

    private readonly NpgsqlConnectionStringBuilder _connection;
    private readonly IProcessRunner _runner;
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly DemoModeOptions? _demo;

    public string BackupRootPath { get; }

    public DatabaseBackupService(
        IConfiguration configuration,
        IProcessRunner runner,
        TimeProvider time,
        ILogger<DatabaseBackupService> logger,
        DemoModeOptions? demo = null)
    {
        _demo = demo;
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Host=kaimo_file_server_db;Database=kaimo_file_server;Username=kaimo_test_user;Password=change_me";
        _connection = new NpgsqlConnectionStringBuilder(connectionString);
        BackupRootPath = configuration["Backup:RootPath"] ?? "/data/kaimo-backups";
        _runner = runner;
        _time = time;
        _logger = logger;
    }

    public async Task<BackupFileInfo> CreateBackupAsync(
        BackupTrigger trigger, CancellationToken cancellationToken = default)
    {
        // A public demo must not let visitors produce dumps on demand. Automatic
        // (scheduled / pre-migration) backups are unaffected.
        if (_demo?.ReadOnly == true && trigger == BackupTrigger.Manual)
            throw new ReadOnlyDemoException();

        EnsureBackupDirectoryWritable();

        var createdAt = _time.GetLocalNow();
        var fileName = BackupFileNaming.Build(createdAt, trigger);
        var targetPath = Path.Combine(BackupRootPath, fileName);

        var arguments = new List<string>
        {
            "--format=custom",
            "--no-owner",
            "--no-privileges",
            "--file", targetPath,
            "--host", _connection.Host ?? "kaimo_file_server_db",
            "--port", (_connection.Port).ToString(),
            "--username", _connection.Username ?? "kaimo_test_user",
            "--dbname", _connection.Database ?? "kaimo_file_server",
        };

        _logger.LogInformation(
            "Creating {Trigger} database backup at {Path}.", trigger, targetPath);

        var result = await _runner.RunAsync("pg_dump", arguments, PasswordEnvironment(), cancellationToken);
        if (!result.Succeeded)
        {
            TryDeletePartial(targetPath);
            throw new InvalidOperationException(
                $"pg_dump failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        RestrictToOwner(targetPath);
        var size = new FileInfo(targetPath).Length;
        _logger.LogInformation(
            "Database backup {FileName} created ({Size} bytes).", fileName, size);
        return new BackupFileInfo(fileName, size, createdAt, trigger);
    }

    /// <summary>
    /// A dump holds every password hash and protected credential; pg_dump creates
    /// it with the umask default (typically 0644), so tighten it to owner-only.
    /// </summary>
    internal void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not restrict permissions of backup {Path}.", path);
        }
    }

    public IReadOnlyList<BackupFileInfo> ListBackups()
    {
        if (!Directory.Exists(BackupRootPath))
            return [];

        var backups = new List<BackupFileInfo>();
        foreach (var path in Directory.EnumerateFiles(BackupRootPath, "*" + BackupFileNaming.Extension))
        {
            var name = Path.GetFileName(path);
            var parsed = BackupFileNaming.TryParse(name);
            if (parsed is null)
                continue;

            long size;
            try { size = new FileInfo(path).Length; }
            catch (FileNotFoundException) { continue; }

            backups.Add(new BackupFileInfo(name, size, parsed.Value.CreatedAtLocal, parsed.Value.Trigger));
        }

        return backups
            .OrderByDescending(b => b.CreatedAtLocal)
            .ToList();
    }

    public string? ResolveBackupPath(string fileName)
    {
        // Reject anything that is not a bare file name matching the scheme.
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName != Path.GetFileName(fileName)
            || BackupFileNaming.TryParse(fileName) is null)
            return null;

        var fullPath = Path.Combine(BackupRootPath, fileName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    public async Task RestoreAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Backup file to restore was not found.", path);

        var arguments = new List<string>
        {
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-privileges",
            "--host", _connection.Host ?? "kaimo_file_server_db",
            "--port", (_connection.Port).ToString(),
            "--username", _connection.Username ?? "kaimo_test_user",
            "--dbname", _connection.Database ?? "kaimo_file_server",
            path,
        };

        _logger.LogWarning("Restoring database from {Path} (this overwrites current data).", path);
        var result = await _runner.RunAsync("pg_restore", arguments, PasswordEnvironment(), cancellationToken);

        // pg_restore reports non-fatal issues (e.g. DROP of a not-yet-existing
        // object with --if-exists) with a non-zero exit code and "warning:" text.
        // Treat exit code 1 as a warning as long as there is no "error:" line.
        if (!result.Succeeded)
        {
            var hasError = result.StandardError.Contains("error:", StringComparison.OrdinalIgnoreCase);
            if (result.ExitCode == 1 && !hasError)
            {
                _logger.LogWarning(
                    "pg_restore finished with warnings:\n{Warnings}", result.StandardError.Trim());
            }
            else
            {
                throw new InvalidOperationException(
                    $"pg_restore failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
            }
        }

        _logger.LogWarning("Database restore from {Path} completed.", path);
    }

    public Task PruneAsync(BackupSettings settings, CancellationToken cancellationToken = default)
    {
        var now = _time.GetLocalNow();
        var backups = ListBackups();

        var toDelete = new List<BackupFileInfo>();

        // Scheduled backups: keep the newest RetentionCount and drop anything
        // older than RetentionDays.
        var scheduled = backups.Where(b => b.Trigger == BackupTrigger.Scheduled).ToList();
        for (int i = 0; i < scheduled.Count; i++)
        {
            var backup = scheduled[i];
            var tooMany = i >= settings.RetentionCount;
            var tooOld = (now - backup.CreatedAtLocal).TotalDays > settings.RetentionDays;
            if (tooMany || tooOld)
                toDelete.Add(backup);
        }

        // Pre-migration backups: age-based only, with a longer retention.
        foreach (var backup in backups.Where(b => b.Trigger == BackupTrigger.PreMigration))
        {
            if ((now - backup.CreatedAtLocal).TotalDays > PreMigrationRetentionDays)
                toDelete.Add(backup);
        }

        // Manual backups are never auto-deleted — the user created them on purpose.

        foreach (var backup in toDelete)
        {
            try
            {
                File.Delete(Path.Combine(BackupRootPath, backup.FileName));
                _logger.LogInformation("Pruned old backup {FileName}.", backup.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune backup {FileName}.", backup.FileName);
            }
        }

        return Task.CompletedTask;
    }

    // Fail fast with an actionable message instead of letting pg_dump surface a
    // bare "Permission denied". The dump runs as the container's non-root user, so
    // the mounted host backup directory must be writable by that account — a common
    // trip-up when the backup volume is first added to an existing deployment.
    private void EnsureBackupDirectoryWritable()
    {
        try
        {
            Startup.WritableDirectoryCheck.Probe(BackupRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Backup directory '{BackupRootPath}' is not writable by the service account. " +
                "Grant the container user write access to the mounted host directory " +
                $"(e.g. 'chown -R 1654:1654 <host path bound to {BackupRootPath}>') and restart.",
                ex);
        }
    }

    private Dictionary<string, string> PasswordEnvironment()
    {
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(_connection.Password))
            env["PGPASSWORD"] = _connection.Password;
        return env;
    }

    private void TryDeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete partial backup {Path}.", path); }
    }
}
