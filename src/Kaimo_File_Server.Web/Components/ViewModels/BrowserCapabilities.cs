namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Describes the operations and optional UI features exposed by one browser backend.
/// The shared file-browser UI uses these flags instead of assuming a local filesystem.
/// </summary>
public sealed record BrowserCapabilities
{
    public bool CanOpen { get; init; } = true;
    public bool CanUpload { get; init; }
    public bool CanCreateDirectory { get; init; }
    public bool CanRename { get; init; }
    public bool CanDelete { get; init; }
    public bool CanMove { get; init; }
    public bool CanCopy { get; init; }
    /// <summary>
    /// Indicates whether items from this browser may be removed after a paste.
    /// Remote shares intentionally never expose this capability: a cross-share
    /// transfer from a virtual provider is always a copy.
    /// </summary>
    public bool CanCut { get; init; }
    public bool CanCopyToLocal { get; init; }
    public bool CanArchive { get; init; }
    public bool CanExtract { get; init; }
    public bool HasFileAcls { get; init; }
    public bool HasVersions { get; init; }
    public bool HasProperties { get; init; } = true;
    public bool HasCloudSync { get; init; }
    public bool HasSearchIntegration { get; init; }
    public bool ShowDirectorySizes { get; init; }

    /// <summary>
    /// Same backend with every write/mutation action removed (read-only demo mode).
    /// View features — ACLs, versions, properties, search, downloads — stay on so the
    /// demo can still show everything; only actions that would change data are dropped.
    /// </summary>
    public BrowserCapabilities AsReadOnly() => this with
    {
        CanUpload = false,
        CanCreateDirectory = false,
        CanRename = false,
        CanDelete = false,
        CanMove = false,
        CanCopy = false,
        CanCut = false,
        CanArchive = false,
        CanExtract = false,
    };

    public static BrowserCapabilities Local { get; } = new()
    {
        CanOpen = true,
        CanUpload = true,
        CanCreateDirectory = true,
        CanRename = true,
        CanDelete = true,
        CanMove = true,
        CanCopy = true,
        CanCut = true,
        CanArchive = true,
        CanExtract = true,
        HasFileAcls = true,
        HasVersions = true,
        HasProperties = true,
        HasCloudSync = true,
        HasSearchIntegration = true,
        ShowDirectorySizes = true
    };
}
