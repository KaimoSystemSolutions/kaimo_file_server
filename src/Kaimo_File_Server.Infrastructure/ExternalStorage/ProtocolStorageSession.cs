using System.Text;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal sealed class ProtocolStorageSession(
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
internal static class ProtocolTemporaryFile
{
    public static string CreatePath() => Path.Combine(Path.GetTempPath(), $"kaimo-protocol-{Guid.NewGuid():N}");

    /// <summary>A read/write owner-only temp file that deletes itself on dispose.</summary>
    public static FileStream CreateSpool()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        // The setter itself throws on Windows, even for null.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(CreatePath(), options);
    }
    public static FileStream OpenDeleteOnClose(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    public static async Task WriteRestrictedTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        }
        else
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }
        await using (stream)
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
    }

    public static async Task WriteRestrictedBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        FileStream stream = OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        await using (stream)
            await stream.WriteAsync(content, cancellationToken);
    }
}
internal static class ProtocolStringExtensions
{
    public static bool ContainsAny(this string value, params char[] candidates)
        => value.IndexOfAny(candidates) >= 0;
}
