using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Batches (delete, paste, drag of several items) pinned to the virtual share they started in.
/// A load swaps connection and share before the new listing arrives, so a batch confirmed
/// meanwhile, or still running, must not resolve its paths against the browser's current state.
/// </summary>
public sealed partial class RemoteCloudAccessFileBrowserViewModel
{
    // The connection, share and write permission of a loaded listing. Opening a share on another
    // connection disposes the old session: a batch pinned to it then fails its remaining items
    // instead of acting on the newly opened share.
    private sealed record RemoteTarget(IRemoteFileStore Files, CloudAccessShare Share, bool Writable)
        : FileOperationTarget;

    // The share whose listing is on screen, set when a load completes and kept while the next
    // one runs, so a batch confirmed meanwhile acts on the items the user sees.
    private RemoteTarget? _shownTarget;

    public override FileOperationTarget? CaptureOperationTarget() => _shownTarget;

    public override async Task CreateFolderAtAsync(string path, FileOperationTarget? target)
    {
        var result = await RunWriteAsync(target,
            pin => pin.Files.CreateDirectoryAsync(RemotePath(pin.Share, path)),
            R("Web_CloudAccess_Error_CreateFolder"));
        if (!result.Success) throw new IOException(result.Error);
    }

    public override Task<OperationResult> DeleteAsync(FileMetadata item, FileOperationTarget? target)
        => RunWriteAsync(target,
            pin => pin.Files.DeleteAsync(RemotePath(pin.Share, ShareRelativeOf(item)), item.IsDirectory),
            R("Web_CloudAccess_Error_Delete"));

    public override Task<OperationResult> MoveAsync(
        FileMetadata item, string destinationPath, FileOperationTarget? target)
        => RunWriteAsync(target, pin => pin.Files.MoveAsync(
                RemotePath(pin.Share, ShareRelativeOf(item)),
                RemotePath(pin.Share, ShareRelativePath.Combine(destinationPath, item.Name))),
            R("Web_CloudAccess_Error_Move"));

    public override Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath, FileOperationTarget? target)
        => ListCoreAsync(target, ShareRelativeOf(new FileMetadata { Path = directoryPath }));

    public override async Task CopyAsync(
        FileMetadata item, string targetPath, CancellationToken cancellationToken, FileOperationTarget? target)
    {
        var result = await RunWriteAsync(target, pin => CopyRemoteItemAsync(pin.Files,
                RemotePath(pin.Share, ShareRelativeOf(item)), RemotePath(pin.Share, targetPath),
                item.IsDirectory, cancellationToken),
            R("Web_CloudAccess_Error_RemoteCopy"));
        if (!result.Success) throw new IOException(result.Error);
    }
}
