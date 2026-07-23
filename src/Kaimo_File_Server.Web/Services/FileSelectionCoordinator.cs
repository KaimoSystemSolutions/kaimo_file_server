namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Coordinates a one-time file selection between the global search in the layout
/// and the currently rendered file browser without exposing UI state in the URL.
/// </summary>
public sealed class FileSelectionCoordinator
{
    private FileSelectionRequest? _pendingRequest;

    public event Action? SelectionRequested;

    /// <returns><see langword="true"/> when the open file browser handled the request immediately.</returns>
    public bool RequestSelection(string shareName, string subPath, string itemName)
    {
        _pendingRequest = new FileSelectionRequest(shareName, NormalizePath(subPath), itemName);
        SelectionRequested?.Invoke();
        return _pendingRequest is null;
    }

    public bool TryConsume(string shareName, string subPath, out string itemName)
    {
        var request = _pendingRequest;
        if (request is null
            || !string.Equals(request.ShareName, shareName, StringComparison.Ordinal)
            || !string.Equals(request.SubPath, NormalizePath(subPath), StringComparison.Ordinal))
        {
            itemName = "";
            return false;
        }

        _pendingRequest = null;
        itemName = request.ItemName;
        return true;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private sealed record FileSelectionRequest(string ShareName, string SubPath, string ItemName);
}
