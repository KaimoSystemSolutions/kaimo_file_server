using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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

/// <summary>
/// Typed wrapper around the restic CLI. Secrets travel only through the environment; the
/// child process never inherits the Web process environment (database connection string).
/// </summary>
public sealed class ResticClient(IProcessRunner runner, IConfiguration configuration)
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

    // ── argument building ───────────────────────────────────────────────

    /// <summary>
    /// <c>forget</c> for one job: grouped by host and paths (one group per share), never by
    /// tags — the unique <c>run:</c> tag would make every snapshot its own group and keep all.
    /// </summary>
    internal static List<string> BuildForgetArgs(Core.Domain.Backup.BackupRetention retention, Guid jobId, bool systemPointsOnly = false)
    {
        if (retention.IsEmpty)
            throw new ResticException("retention_empty", "A retention without any rule would keep or remove everything.");
        // "--tag a,b" matches points carrying both tags.
        var args = systemPointsOnly
            ? new List<string> { "forget", "--tag", "job:" + jobId.ToString("N") + ",kind:system", "--group-by", "host" }
            : new List<string> { "forget", "--tag", "job:" + jobId.ToString("N"), "--group-by", "host,paths" };
        void Keep(string flag, int value)
        {
            if (value > 0)
                args.AddRange([flag, value.ToString(CultureInfo.InvariantCulture)]);
        }
        Keep("--keep-last", retention.KeepLast);
        Keep("--keep-daily", retention.KeepDaily);
        Keep("--keep-weekly", retention.KeepWeekly);
        Keep("--keep-monthly", retention.KeepMonthly);
        Keep("--keep-yearly", retention.KeepYearly);
        return args;
    }

    /// <summary>Sums the "remove" lists of <c>forget --json</c> (one array of groups).</summary>
    internal static int CountForgotten(IEnumerable<string> lines)
    {
        var removed = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '[')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                foreach (var group in doc.RootElement.EnumerateArray())
                {
                    if (group.ValueKind == JsonValueKind.Object
                        && group.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.Array)
                        removed += remove.GetArrayLength();
                }
            }
            catch (JsonException) { }
        }
        return removed;
    }

    /// <summary>
    /// One invocation for <c>restic copy</c>: the destination is the main repository, the source
    /// goes into <c>RESTIC_FROM_*</c>. Backend variables and options are not namespaced per
    /// repository, so differing values for the same name cannot work and are rejected (the
    /// mirror form validates this up front). Both <c>sftp.command</c>s of one SSH connection only
    /// differ in their temporary key path, so the destination's serves both.
    /// </summary>
    internal static ResticTarget BuildCopyTarget(ResticTarget destination, ResticTarget source)
    {
        var env = new Dictionary<string, string>(destination.Environment);
        foreach (var (key, value) in source.Environment)
        {
            switch (key)
            {
                case "RESTIC_REPOSITORY": env["RESTIC_FROM_REPOSITORY"] = value; break;
                case "RESTIC_PASSWORD": env["RESTIC_FROM_PASSWORD"] = value; break;
                default:
                    if (env.TryGetValue(key, out var existing) && existing != value)
                        throw new ResticException("mirror_conflict", $"Both repositories need different values for {key}.");
                    env[key] = value;
                    break;
            }
        }

        var args = destination.GlobalArguments.ToList();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Count; i += 2)
            if (args[i] == "-o")
                options[OptionName(args[i + 1])] = args[i + 1];
        for (var i = 0; i + 1 < source.GlobalArguments.Count; i += 2)
        {
            var (flag, value) = (source.GlobalArguments[i], source.GlobalArguments[i + 1]);
            if (flag == "-o" && options.TryGetValue(OptionName(value), out var current))
            {
                if (current != value && OptionName(value) != "sftp.command")
                    throw new ResticException("mirror_conflict", $"Both repositories need different values for {OptionName(value)}.");
                continue;
            }
            // --cacert may repeat; every listed CA is trusted for both repositories.
            args.AddRange([flag, value]);
        }
        return new ResticTarget(destination.RepositoryUrl, env, args, null);

        static string OptionName(string option) => option.Split('=', 2)[0];
    }

    internal static List<string> BuildBackupArgs(ResticBackupRequest request)
    {
        if (request.Paths.Count == 0)
            throw new ResticException("source_missing", "A backup needs at least one path.");
        var args = new List<string> { "backup", "--host", HostName };
        foreach (var tag in request.Tags)
            args.AddRange(["--tag", tag]);
        foreach (var exclude in request.Excludes)
            args.AddRange(["--exclude", exclude]);
        // Separate options from paths so a path can never be read as a flag.
        args.Add("--");
        args.AddRange(request.Paths);
        return args;
    }

    internal static List<string> BuildRestoreArgs(string snapshotId, string folder, IReadOnlyList<string> names, string targetDirectory)
    {
        // "<snapshot>:<folder>" makes the folder the restore root, so includes are "/<name>".
        var args = new List<string> { "restore", "--target", targetDirectory, "--verify" };
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('/') || name is "." or "..")
                throw new ResticException("path_invalid", "Only entries of the selected folder can be restored.");
            args.AddRange(["--include", "/" + EscapePattern(name)]);
        }
        args.Add("--");
        args.Add(snapshotId + ":" + TrimDirectory(folder));
        return args;
    }

    internal static List<string> BuildRestorePathsArgs(string snapshotId, IReadOnlyList<string> absolutePaths, string targetDirectory)
    {
        if (absolutePaths.Count == 0)
            throw new ResticException("path_invalid", "Nothing to restore.");
        var args = new List<string> { "restore", "--target", targetDirectory, "--verify" };
        foreach (var path in absolutePaths)
        {
            var p = TrimDirectory(path);
            if (!p.StartsWith('/') || p.Split('/').Any(s => s is "." or ".."))
                throw new ResticException("path_invalid", "Only absolute snapshot paths can be restored.");
            args.AddRange(["--include", EscapePattern(p)]);
        }
        args.AddRange(["--", snapshotId]);
        return args;
    }

    /// <summary>restic include/exclude values are glob patterns; a literal name must escape them.</summary>
    internal static string EscapePattern(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (c is '\\' or '*' or '?' or '[')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Snapshot paths are absolute POSIX paths; "/" stays the root.</summary>
    internal static string TrimDirectory(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    internal static ResticNode? ParseNode(string line)
    {
        using var doc = TryParse(line);
        if (doc is null)
            return null;
        var root = doc.RootElement;
        // restic ≥ 0.17 sets message_type, older versions struct_type.
        if ((GetString(root, "message_type") ?? GetString(root, "struct_type")) != "node")
            return null;
        var path = GetString(root, "path");
        var name = GetString(root, "name");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
            return null;
        DateTime? modified = DateTimeOffset.TryParse(GetString(root, "mtime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.UtcDateTime
            : null;
        return new ResticNode(name, path, GetString(root, "type") == "dir", GetLong(root, "size"), modified);
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

    // ── backup output parsing ───────────────────────────────────────────

    private sealed class BackupState
    {
        public string? SnapshotId;
        public long FilesNew, FilesChanged, FilesUnmodified, BytesAdded, BytesProcessed;
        public readonly List<string> Warnings = [];
        public int WarningCount;
    }

    private static ResticBackupResult ToResult(BackupState state, ProcessResult result)
    {
        // Exit code 3 without parsed error lines still means "incomplete snapshot".
        var warnings = Math.Max(state.WarningCount, result.ExitCode == 3 ? 1 : 0);
        return new ResticBackupResult(
            state.SnapshotId, state.FilesNew, state.FilesChanged, state.FilesUnmodified,
            state.BytesAdded, state.BytesProcessed, warnings, state.Warnings, result.StandardError);
    }

    /// <summary>Parses all backup output lines at once (used by tests and replay).</summary>
    internal static ResticBackupResult ParseBackupOutput(IEnumerable<string> lines, ProcessResult result)
    {
        var state = new BackupState();
        foreach (var line in lines)
            ApplyBackupLine(state, line, null);
        return ToResult(state, result);
    }

    private const int MaximumKeptWarnings = 50;

    private static void ApplyBackupLine(BackupState state, string line, Action<ResticProgress>? onProgress)
    {
        using var doc = TryParse(line);
        if (doc is null)
            return;
        var root = doc.RootElement;
        switch (Type(doc))
        {
            case "status":
                onProgress?.Invoke(new ResticProgress(
                    GetDouble(root, "percent_done"),
                    GetLong(root, "files_done"), GetLong(root, "total_files"),
                    GetLong(root, "bytes_done"), GetLong(root, "total_bytes")));
                break;
            case "error":
                state.WarningCount++;
                if (state.Warnings.Count < MaximumKeptWarnings)
                {
                    var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                        ? GetString(error, "message")
                        : null;
                    var item = GetString(root, "item");
                    state.Warnings.Add(string.IsNullOrEmpty(item) ? message ?? "error" : $"{item}: {message}");
                }
                break;
            case "summary":
                state.SnapshotId = GetString(root, "snapshot_id");
                state.FilesNew = GetLong(root, "files_new");
                state.FilesChanged = GetLong(root, "files_changed");
                state.FilesUnmodified = GetLong(root, "files_unmodified");
                state.BytesAdded = GetLong(root, "data_added");
                state.BytesProcessed = GetLong(root, "total_bytes_processed");
                break;
        }
    }

    internal static IReadOnlyList<ResticSnapshotInfo> ParseSnapshots(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<ResticSnapshotInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                var time = GetString(item, "time");
                var parsedTime = DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t.UtcDateTime
                    : DateTime.MinValue;
                long files = 0, bytes = 0;
                if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
                {
                    files = GetLong(summary, "total_files_processed");
                    bytes = GetLong(summary, "total_bytes_processed");
                }
                list.Add(new ResticSnapshotInfo(id, parsedTime, GetStrings(item, "paths"), GetStrings(item, "tags"),
                    GetString(item, "hostname"), files, bytes));
            }
            return list;
        }
        catch (JsonException ex)
        {
            throw new ResticException("unexpected_output", "restic returned an unreadable snapshot list.", ex);
        }
    }

    // ── exit codes ──────────────────────────────────────────────────────

    /// <summary>Maps a restic exit code (and, for exit 1, known messages) to a stable error code.</summary>
    public static string MapErrorCode(int exitCode, string stderr) => exitCode switch
    {
        10 => "repo_not_found",
        11 => "lock_failed",
        12 => "wrong_password",
        130 => "cancelled",
        1 when stderr.Contains("config file already exists", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("repository master key and config already initialized", StringComparison.OrdinalIgnoreCase) => "repo_exists",
        1 when stderr.Contains("wrong password", StringComparison.OrdinalIgnoreCase) => "wrong_password",
        1 when stderr.Contains("Is there a repository at the following location", StringComparison.OrdinalIgnoreCase) => "repo_not_found",
        1 when stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase) => "permission_denied",
        1 when stderr.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("ssh command exited", StringComparison.OrdinalIgnoreCase) => "repo_unreachable",
        1 when stderr.Contains("certificate", StringComparison.OrdinalIgnoreCase) => "tls_error",
        1 when stderr.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) => "access_denied",
        _ => "restic_failed",
    };

    private static void ThrowOnFailure(ProcessResult result, string command)
    {
        if (result.ExitCode == 0)
            return;
        var code = MapErrorCode(result.ExitCode, result.StandardError);
        throw new ResticException(code, $"restic {command} failed with exit code {result.ExitCode} ({code}).")
        {
            Data = { ["stderr"] = result.StandardError },
        };
    }

    // ── tolerant JSON helpers ───────────────────────────────────────────

    private static JsonDocument? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            return null;
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    private static string? Type(JsonDocument doc) => GetString(doc.RootElement, "message_type");

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    private static double GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];
}
