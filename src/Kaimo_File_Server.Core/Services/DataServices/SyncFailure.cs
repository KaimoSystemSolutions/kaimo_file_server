namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>The transfer step that failed for a single item during a sync run.</summary>
public enum SyncFailureOperation
{
    Upload,
    Download,
    Delete,
    List,
    CreateDirectory
}

/// <summary>
/// One item that could not be synchronized. Collected instead of aborting the
/// whole run so a single unreadable or rejected file does not stop a large
/// directory sync; the run reports every failure at the end.
/// </summary>
public sealed record SyncFailure(
    string Path,
    SyncFailureOperation Operation,
    string Reason);
