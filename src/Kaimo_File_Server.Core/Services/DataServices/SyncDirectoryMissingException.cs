namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// A sync endpoint directory no longer exists. Carries which side failed so the
/// health surface can show an explicit "local folder removed" / "remote folder
/// removed" message instead of a generic sync failure.
/// </summary>
public sealed class SyncDirectoryMissingException(SyncEndpoint endpoint, Exception? innerException = null)
    : Exception(
        endpoint == SyncEndpoint.Local
            ? "The local sync directory no longer exists."
            : "The remote sync directory no longer exists.",
        innerException)
{
    public SyncEndpoint Endpoint { get; } = endpoint;
}
