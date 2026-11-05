namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Where a new virtual share is mounted when it is created as a folder inside a local share.
/// An empty <paramref name="FolderName"/> falls back to the share name.
/// </summary>
public sealed record CloudAccessMountRequest(Guid HostShareId, string ParentPath, string? FolderName);
