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
    public bool CanArchive { get; init; }
    public bool CanExtract { get; init; }
    public bool HasFileAcls { get; init; }
    public bool HasVersions { get; init; }
    public bool HasProperties { get; init; } = true;
    public bool HasCloudSync { get; init; }
    public bool HasSearchIntegration { get; init; }
    public bool ShowDirectorySizes { get; init; }

    public static BrowserCapabilities Local { get; } = new()
    {
        CanOpen = true,
        CanUpload = true,
        CanCreateDirectory = true,
        CanRename = true,
        CanDelete = true,
        CanMove = true,
        CanCopy = true,
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
