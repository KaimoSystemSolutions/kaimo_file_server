namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>Which side of a sync a directory-missing failure came from.</summary>
public enum SyncEndpoint
{
    Local,
    Remote
}
