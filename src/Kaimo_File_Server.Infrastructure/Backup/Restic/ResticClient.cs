using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Typed wrapper around the restic CLI. Secrets travel only through the environment; the
/// child process never inherits the Web process environment (database connection string).
/// </summary>
public sealed partial class ResticClient(IProcessRunner runner, IConfiguration configuration)
{
    /// <summary>restic's fixed host name for every Kaimo snapshot: the container host name
    /// changes on every recreate and would break parent-snapshot detection.</summary>
    public const string HostName = "kaimo";

    private string Binary => configuration["Backup:Restic:Binary"] ?? "restic";

    public string CacheDirectory => configuration["Backup:Restic:CacheDirectory"]
        ?? Path.Combine(configuration["Storage:ApplicationDataPath"] ?? "/data/kaimo-system", ".kaimo-restic-cache");

    /// <summary>Creates a new repository; returns its id.</summary>
    public async Task<string> InitAsync(ResticTarget target, CancellationToken ct)
    {
        string? id = null;
        var result = await RunAsync(target, ["init"], line =>
        {
            if (TryParse(line) is { } doc && Type(doc) == "initialized")
                id = GetString(doc.RootElement, "id");
        }, ct, lockRetry: null);
        ThrowOnFailure(result, "init");
        return id ?? await CatConfigAsync(target, ct);
    }

    /// <summary>Reads the repository config (also proves URL, credentials and password); returns the repository id.</summary>
    public async Task<string> CatConfigAsync(ResticTarget target, CancellationToken ct)
    {
        var lines = new List<string>();
        var result = await RunAsync(target, ["cat", "config"], lines.Add, ct, lockRetry: null);
        ThrowOnFailure(result, "cat config");
        var json = string.Join('\n', lines);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetString(doc.RootElement, "id")
                   ?? throw new ResticException("unexpected_output", "restic did not report a repository id.");
        }
        catch (JsonException ex)
        {
            throw new ResticException("unexpected_output", "restic returned an unreadable repository config.", ex);
        }
    }

    /// <summary>The installed restic version line (e.g. "restic 0.19.1 compiled with …"), or "unknown".</summary>
    public async Task<string> VersionAsync(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunStreamingAsync(Binary, ["version"], null, null, clearEnvironment: true, ct);
            var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return result.ExitCode == 0 && !string.IsNullOrEmpty(line) ? line : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Removes stale locks only (never <c>--remove-all</c>).</summary>
    public async Task UnlockAsync(ResticTarget target, CancellationToken ct)
    {
        var result = await RunAsync(target, ["unlock"], null, ct, lockRetry: null);
        ThrowOnFailure(result, "unlock");
    }

    public async Task<ResticBackupResult> BackupAsync(
        ResticTarget target, ResticBackupRequest request, Action<ResticProgress>? onProgress, CancellationToken ct)
    {
        var state = new BackupState();
        var result = await RunAsync(target, BuildBackupArgs(request), line => ApplyBackupLine(state, line, onProgress), ct, lockRetry: "30m");
        if (result.ExitCode is not (0 or 3))
            ThrowOnFailure(result, "backup");
        return ToResult(state, result);
    }

    public async Task<IReadOnlyList<ResticSnapshotInfo>> SnapshotsAsync(ResticTarget target, CancellationToken ct)
    {
        var lines = new List<string>();
        var result = await RunAsync(target, ["snapshots"], lines.Add, ct, lockRetry: "1m");
        ThrowOnFailure(result, "snapshots");
        return ParseSnapshots(string.Join('\n', lines));
    }

    /// <summary>The direct children of <paramref name="directory"/> (an absolute path inside the snapshot).</summary>
    public async Task<IReadOnlyList<ResticNode>> ListDirectoryAsync(
        ResticTarget target, string snapshotId, string directory, CancellationToken ct)
    {
        var dir = TrimDirectory(directory);
        var nodes = new List<ResticNode>();
        // restic also prints the listed directory itself.
        await ListAsync(target, snapshotId, dir, recursive: false, node => { if (node.Path != dir) nodes.Add(node); }, ct);
        return nodes;
    }

    /// <summary>Streams the nodes below <paramref name="path"/> (the path itself included).</summary>
    public async Task ListAsync(
        ResticTarget target, string snapshotId, string path, bool recursive, Action<ResticNode> onNode, CancellationToken ct)
    {
        var args = new List<string> { "ls" };
        if (recursive)
            args.Add("--recursive");
        args.AddRange(["--", snapshotId, TrimDirectory(path)]);
        var result = await RunAsync(target, args, line =>
        {
            if (ParseNode(line) is { } node)
                onNode(node);
        }, ct, lockRetry: "10s");
        ThrowOnFailure(result, "ls");
    }

    /// <summary>
    /// Restores entries of <paramref name="folder"/> into <paramref name="targetDirectory"/>:
    /// the named children only, or all of them when <paramref name="names"/> is empty.
    /// <c>--verify</c> reads every restored file back.
    /// </summary>
    public async Task<ResticRestoreResult> RestoreAsync(
        ResticTarget target, string snapshotId, string folder, IReadOnlyList<string> names, string targetDirectory,
        Action<double>? onProgress, CancellationToken ct)
        => await RunRestoreAsync(target, BuildRestoreArgs(snapshotId, folder, names, targetDirectory), onProgress, ct);

    /// <summary>
    /// Restores single entries by their absolute snapshot path into
    /// <c>&lt;targetDirectory&gt;/&lt;absolute path&gt;</c> (test restore).
    /// </summary>
    public async Task<ResticRestoreResult> RestorePathsAsync(
        ResticTarget target, string snapshotId, IReadOnlyList<string> absolutePaths, string targetDirectory, CancellationToken ct)
        => await RunRestoreAsync(target, BuildRestorePathsArgs(snapshotId, absolutePaths, targetDirectory), null, ct);

    /// <summary>Restores a whole snapshot; its absolute paths are recreated below <paramref name="targetDirectory"/>.</summary>
    public async Task<ResticRestoreResult> RestoreSnapshotAsync(
        ResticTarget target, string snapshotId, string targetDirectory, Action<double>? onProgress, CancellationToken ct)
        => await RunRestoreAsync(target, ["restore", "--target", targetDirectory, "--verify", "--", snapshotId], onProgress, ct);

    private async Task<ResticRestoreResult> RunRestoreAsync(
        ResticTarget target, IReadOnlyList<string> args, Action<double>? onProgress, CancellationToken ct)
    {
        long files = 0, bytes = 0;
        var errors = new List<string>();
        var result = await RunAsync(target, args, line =>
        {
            using var doc = TryParse(line);
            if (doc is null)
                return;
            var root = doc.RootElement;
            switch (Type(doc))
            {
                case "status":
                    onProgress?.Invoke(GetDouble(root, "percent_done"));
                    break;
                case "error" when errors.Count < MaximumKeptWarnings:
                    var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                        ? GetString(error, "message") : null;
                    errors.Add($"{GetString(root, "item")}: {message}");
                    break;
                case "summary":
                    files = GetLong(root, "files_restored");
                    bytes = GetLong(root, "bytes_restored");
                    break;
            }
        }, ct, lockRetry: "30m");
        if (result.ExitCode != 0 && errors.Count > 0)
            result = result with { StandardError = string.Join('\n', errors) + "\n" + result.StandardError };
        ThrowOnFailure(result, "restore");
        return new ResticRestoreResult(files, bytes);
    }

    /// <summary>
    /// Writes one file (raw) or one directory (as ZIP archive) of the snapshot to <paramref name="output"/>.
    /// Runs without <c>--json</c>, so nothing but the content ever reaches the output.
    /// </summary>
    public async Task DumpAsync(
        ResticTarget target, string snapshotId, string path, bool asZipArchive, Stream output, CancellationToken ct)
    {
        var args = BuildGlobalArgs(target, json: false, lockRetry: "10s");
        args.Add("dump");
        if (asZipArchive)
            args.AddRange(["--archive", "zip"]);
        // "<snapshot>:<parent>" makes the parent the root, so a ZIP holds "<name>/…" instead of
        // the full server path of the share.
        var full = TrimDirectory(path);
        var cut = full.LastIndexOf('/');
        var parent = cut > 0 ? full[..cut] : "/";
        args.AddRange(["--", snapshotId + ":" + parent, full[cut..]]);
        ProcessResult result;
        try
        {
            result = await runner.RunToStreamAsync(Binary, args, target.Environment, output, clearEnvironment: true, ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ResticException("restic_unavailable", "The restic binary is not installed.", ex);
        }
        ThrowOnFailure(result, "dump");
    }

    /// <summary>
    /// Applies a job's retention to its backup points; returns how many were removed. With
    /// <paramref name="includeSystemPass"/> a second pass applies it to the job's system backup
    /// points as one group: their paths change with the content level (or an external
    /// certificate), and a path group nobody writes to anymore would keep its points forever.
    /// </summary>
    public async Task<int> ForgetAsync(
        ResticTarget target, Guid jobId, Core.Domain.Backup.BackupRetention retention, CancellationToken ct, bool includeSystemPass = false)
    {
        var removed = 0;
        foreach (var args in includeSystemPass
                     ? new[] { BuildForgetArgs(retention, jobId), BuildForgetArgs(retention, jobId, systemPointsOnly: true) }
                     : new[] { BuildForgetArgs(retention, jobId) })
        {
            var lines = new List<string>();
            var result = await RunAsync(target, args, lines.Add, ct, lockRetry: "30m");
            ThrowOnFailure(result, "forget");
            removed += CountForgotten(lines);
        }
        return removed;
    }

    /// <summary>Deletes data no longer referenced by any backup point.</summary>
    public async Task PruneAsync(ResticTarget target, CancellationToken ct)
    {
        var result = await RunAsync(target, ["prune"], null, ct, lockRetry: "30m");
        ThrowOnFailure(result, "prune");
    }

    /// <summary>
    /// Verifies the repository structure; with <paramref name="readDataSubset"/> (e.g. "3/12")
    /// also reads and verifies that part of the pack data. Damage is a result, not an exception;
    /// an unreachable repository still throws.
    /// </summary>
    public async Task<ResticCheckResult> CheckAsync(ResticTarget target, string? readDataSubset, CancellationToken ct)
    {
        // restic 0.19: the summary (num_errors) goes to stdout, every finding as a JSON "error"
        // line to stderr.
        long reportedErrors = 0;
        var errors = new List<string>();
        var output = new System.Text.StringBuilder();
        void Collect(string line)
        {
            using var doc = TryParse(line);
            if (doc is null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    output.AppendLine(line);
                return;
            }
            switch (Type(doc))
            {
                case "summary":
                    reportedErrors = GetLong(doc.RootElement, "num_errors");
                    break;
                case "error" when errors.Count < MaximumKeptWarnings:
                    if (GetString(doc.RootElement, "message")?.Trim() is { Length: > 0 } message)
                        errors.Add(message);
                    break;
            }
        }

        var args = new List<string> { "check" };
        if (readDataSubset is not null)
            args.AddRange(["--read-data-subset", readDataSubset]);
        var result = await RunAsync(target, args, Collect, ct, lockRetry: "30m");
        foreach (var line in result.StandardError.Split('\n'))
            Collect(line.TrimEnd('\r'));

        var tail = (string.Join('\n', errors) + "\n" + output).Trim();
        tail = tail.Length <= 8 * 1024 ? tail : tail[^(8 * 1024)..];
        if (result.ExitCode == 0)
            return new ResticCheckResult(true, errors, tail);
        // Only restic's own error count proves damage. Any other abort (unreachable, locked,
        // network drop, out of memory) says nothing about the data and throws like everywhere
        // else, so it never raises a false "repository damaged" alarm.
        if (reportedErrors == 0)
            ThrowOnFailure(result, "check");
        return new ResticCheckResult(false, errors, tail);
    }

    /// <summary>
    /// Copies every backup point of <paramref name="source"/> that is missing in
    /// <paramref name="destination"/> (restic skips points copied before).
    /// </summary>
    public async Task CopyAsync(ResticTarget destination, ResticTarget source, CancellationToken ct)
    {
        var merged = BuildCopyTarget(destination, source);
        var result = await RunAsync(merged, ["copy"], null, ct, lockRetry: "30m");
        ThrowOnFailure(result, "copy");
    }

    // Global flags go BEFORE the command: a command may end with "--" followed by paths,
    // and anything after that separator would be taken as a path to back up.
    private List<string> BuildGlobalArgs(ResticTarget target, bool json, string? lockRetry)
    {
        var args = new List<string>();
        if (json)
            args.Add("--json");
        args.AddRange(["--cache-dir", CacheDirectory]);
        if (lockRetry is not null)
            args.AddRange(["--retry-lock", lockRetry]);
        args.AddRange(target.GlobalArguments);
        return args;
    }

    private async Task<ProcessResult> RunAsync(
        ResticTarget target, IReadOnlyList<string> command, Action<string>? onLine, CancellationToken ct, string? lockRetry)
    {
        var args = BuildGlobalArgs(target, json: true, lockRetry);
        args.AddRange(command);
        // With --json, restic reports fatal errors as an "exit_error" line on stdout instead of
        // stderr; fold them into the error text so mapping and log excerpts see them.
        var exitErrors = new System.Text.StringBuilder();
        void Handle(string line)
        {
            using (var doc = TryParse(line))
            {
                if (doc is not null && Type(doc) == "exit_error")
                {
                    exitErrors.AppendLine(GetString(doc.RootElement, "message"));
                    return;
                }
            }
            onLine?.Invoke(line);
        }

        try
        {
            var result = await runner.RunStreamingAsync(Binary, args, target.Environment, Handle, clearEnvironment: true, ct);
            return exitErrors.Length == 0 ? result : result with { StandardError = result.StandardError + exitErrors };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ResticException("restic_unavailable", "The restic binary is not installed.", ex);
        }
    }

}
