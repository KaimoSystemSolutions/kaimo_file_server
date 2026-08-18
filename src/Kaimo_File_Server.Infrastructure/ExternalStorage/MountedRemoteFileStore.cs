using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>
/// File-store adapter for an operator-managed mount. Every path is contained
/// below the configured root and symbolic-link traversal is rejected.
/// </summary>
internal sealed class MountedRemoteFileStore(string rootPath, bool readOnly) : IRemoteFileStore
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

    public Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string directory = Resolve(path, mustExist: true);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The remote directory does not exist.");
        var result = new List<RemoteStorageItem>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(entry);
            var info = new FileInfo(entry);
            bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            result.Add(new RemoteStorageItem(
                info.Name,
                ToRemotePath(entry),
                isDirectory,
                isDirectory ? null : info.Length,
                info.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<RemoteStorageItem>>(result);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string file = Resolve(path, mustExist: true);
        if (!File.Exists(file))
            throw new FileNotFoundException("The remote file does not exist.");
        Stream stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        string file = Resolve(path, mustExist: false);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using var target = new FileStream(
            file,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        Directory.CreateDirectory(Resolve(path, mustExist: false));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        string target = Resolve(path, mustExist: true);
        if (PathsEqual(target, _root))
            throw new UnauthorizedAccessException("The remote root cannot be deleted.");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive);
        else
            File.Delete(target);
        return Task.CompletedTask;
    }

    public Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        string source = Resolve(sourcePath, mustExist: true);
        string destination = Resolve(destinationPath, mustExist: false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
        return Task.CompletedTask;
    }

    private string Resolve(string path, bool mustExist)
    {
        string relative = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new UnauthorizedAccessException("The remote path is invalid.");
        string candidate = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathsEqual(candidate, _root)
            && !candidate.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison))
            throw new UnauthorizedAccessException("The remote path escapes its configured root.");

        ValidateExistingAncestors(candidate);
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("The remote path does not exist.");
        return candidate;
    }

    private void ValidateExistingAncestors(string candidate)
    {
        RejectReparsePoint(_root);
        string relative = Path.GetRelativePath(_root, candidate);
        string current = _root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Symbolic links are not allowed in mounted remote paths.");
    }

    private string ToRemotePath(string path)
        => "/" + Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private void EnsureWritable()
    {
        if (readOnly)
            throw new NotSupportedException("This remote mount is read-only.");
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal sealed class MountedStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    IRemoteFileStore remoteFiles) : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore RemoteFiles { get; } = remoteFiles;
    IRemoteFileStore? IStorageSession.RemoteFiles => RemoteFiles;
    public IOptimizedStorageSync? OptimizedSync => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
