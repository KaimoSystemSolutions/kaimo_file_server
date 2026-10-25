using System.Globalization;
using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

public partial class ResticClient
{
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
    /// differ in their temporary key path, so the destination's serves both; the same holds for
    /// the GCS key and rclone config files (the mirror validation requires identical content).
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
                // The SSH_ASKPASS helpers of one password connection print the same password.
                case "GOOGLE_APPLICATION_CREDENTIALS" or "RCLONE_CONFIG" or "SSH_ASKPASS": env.TryAdd(key, value); break;
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
}
