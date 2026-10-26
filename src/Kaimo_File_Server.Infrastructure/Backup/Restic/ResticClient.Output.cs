using System.Globalization;
using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class ResticClient
{
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
        // SMB status texts as rclone's SMB client prints them (STATUS_BAD_NETWORK_NAME, STATUS_LOGON_FAILURE).
        1 when stderr.Contains("{Network Name Not Found}", StringComparison.OrdinalIgnoreCase) => "smb_share_not_found",
        1 when stderr.Contains("The attempted logon is invalid", StringComparison.OrdinalIgnoreCase) => "access_denied",
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
