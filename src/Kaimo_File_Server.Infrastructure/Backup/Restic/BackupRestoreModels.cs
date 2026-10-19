using System.Globalization;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain.Backup;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>Where restored entries go. Values are persisted in run details; never renumber.</summary>
public enum BackupRestoreMode
{
    /// <summary>Into a new "Restored …" folder next to the original location.</summary>
    NewFolder = 0,

    /// <summary>Back to the original location; existing files are handled by the conflict policy.</summary>
    InPlace = 1,

    /// <summary>Into a new "Restored …" folder in another share.</summary>
    OtherShare = 2,

    /// <summary>Streamed to the browser (file raw, folder as ZIP); only recorded, never queued.</summary>
    Download = 3,

    /// <summary>
    /// A whole system or pool backup point into a new private folder below the server's backup
    /// folder (Global only). Nothing live is touched.
    /// </summary>
    ServerFolder = 4,
}
/// <summary>What happens when an in-place restore meets an existing entry. Never deletes anything.</summary>
public enum BackupRestoreConflict
{
    /// <summary>Replace the file; its current content is kept as a file version first.</summary>
    Overwrite = 0,
    Skip = 1,

    /// <summary>Restore beside it as "name (restored yyyy-MM-dd).ext".</summary>
    Rename = 2,
}
/// <summary>
/// A restore as requested in the UI: entries of <see cref="FolderPath"/> (share-relative, as
/// recorded in the backup) — the named <see cref="Items"/>, or all entries when empty.
/// </summary>
public sealed record BackupRestoreRequest(
    Guid RepositoryId,
    string SnapshotId,
    Guid ShareId,
    string FolderPath,
    IReadOnlyList<string> Items,
    BackupRestoreMode Mode,
    BackupRestoreConflict Conflict,
    bool RestorePermissions,
    Guid? TargetShareId = null,
    string? TargetFolder = null,
    string? Culture = null)
{
    /// <summary>UI language of the requesting admin: names the "Restored …" folder and rename suffix.</summary>
    // A method, not a property: the request is serialized into the run details.
    public CultureInfo GetUiCulture()
    {
        try { return string.IsNullOrEmpty(Culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(Culture); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
/// <summary>A principal of the backed-up permissions that no longer exists.</summary>
public sealed record BackupSkippedPrincipal(Guid Id, string? Name);
/// <summary>Result of a restore, stored in <see cref="BackupRun.DetailsJson"/> and shown in the activity log.</summary>
public sealed class BackupRestoreOutcome
{
    public string? SourceShareName { get; set; }
    public Guid DestinationShareId { get; set; }
    public string? DestinationShareName { get; set; }
    public string DestinationPath { get; set; } = string.Empty;
    public long FilesRestored { get; set; }
    public long BytesRestored { get; set; }
    public int Overwritten { get; set; }

    /// <summary>Existing files with exactly the backed-up content; left alone.</summary>
    public int Unchanged { get; set; }
    public int VersionsCreated { get; set; }
    public int Skipped { get; set; }
    public int Renamed { get; set; }
    public int AclEntriesApplied { get; set; }
    public List<BackupSkippedPrincipal> SkippedPrincipals { get; set; } = [];

    /// <summary>Selected entries that were missing in the backup or point into a reserved folder.</summary>
    public List<string> SkippedItems { get; set; } = [];
}
/// <summary>Content of <see cref="BackupRun.DetailsJson"/> for restore runs.</summary>
public sealed record BackupRestoreDetails(BackupRestoreRequest Request, BackupRestoreOutcome? Result = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BackupRestoreDetails? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var details = JsonSerializer.Deserialize<BackupRestoreDetails>(json, Json);
            return details?.Request is null ? null : details;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
