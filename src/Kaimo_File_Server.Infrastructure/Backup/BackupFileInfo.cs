using System.Globalization;

namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// Metadata about a single backup file on disk. The values are derived from the
/// file name (timestamp + trigger) and the file-system entry (size).
/// </summary>
public sealed record BackupFileInfo(
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedAtLocal,
    BackupTrigger Trigger);

/// <summary>
/// Naming scheme for backup files: <c>kaimo_yyyyMMdd-HHmmss_&lt;trigger&gt;.dump</c>.
/// A single place builds and parses the name so the scheduler, the retention
/// pruner and the UI stay in sync.
/// </summary>
public static class BackupFileNaming
{
    public const string Prefix = "kaimo_";
    public const string Extension = ".dump";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    public static string Build(DateTimeOffset createdAtLocal, BackupTrigger trigger)
        => $"{Prefix}{createdAtLocal.ToString(TimestampFormat, CultureInfo.InvariantCulture)}_{trigger.ToFileToken()}{Extension}";

    /// <summary>
    /// Parses a backup file name. Returns <c>null</c> for anything that does not
    /// match the scheme (e.g. <c>.done</c> markers or unrelated files).
    /// </summary>
    public static (DateTimeOffset CreatedAtLocal, BackupTrigger Trigger)? TryParse(string fileName)
    {
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(Extension, StringComparison.Ordinal))
            return null;

        var core = fileName[Prefix.Length..^Extension.Length];
        var separator = core.IndexOf('_');
        if (separator <= 0)
            return null;

        var timestampPart = core[..separator];
        var triggerPart = core[(separator + 1)..];

        if (!DateTimeOffset.TryParseExact(
                timestampPart, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var createdAt))
            return null;

        BackupTriggerExtensions.TryParseFileToken(triggerPart, out var trigger);
        return (createdAt, trigger);
    }
}
