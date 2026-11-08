using System.Text;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Microsoft.Extensions.Logging;

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
public static class ProtocolTemporaryFile
{
    /// <summary>Optional operator-chosen directory for temporary secrets that ssh must be able to execute.</summary>
    public const string TempDirectoryKey = "Backup:Restic:TempDirectory";

    internal const string NamePrefix = "kaimo-protocol-";

    public static string CreatePath(string? root = null)
        => Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root, $"{NamePrefix}{Guid.NewGuid():N}");

    /// <summary>
    /// Writes the SSH password (0600) and the <c>SSH_ASKPASS</c> helper (0700) that prints it into
    /// <paramref name="directory"/>. ssh runs the helper instead of prompting, so the password never
    /// appears in arguments or the environment, and the directory must not be on a noexec mount.
    /// Returns the helper path.
    /// </summary>
    internal static async Task<string> WriteAskpassAsync(string directory, string password, CancellationToken cancellationToken)
    {
        string passwordPath = Path.Combine(directory, "ssh-password");
        if (passwordPath.Contains('\''))
            throw new ProtocolConfigurationException("temp_path_invalid", "The temporary directory must not contain quotes.");
        await WriteRestrictedTextAsync(passwordPath, password, cancellationToken);
        string helperPath = Path.Combine(directory, "askpass");
        await WriteRestrictedTextAsync(helperPath, $"#!/bin/sh\nexec /bin/cat '{passwordPath}'\n", cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(helperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return helperPath;
    }

    /// <summary>
    /// Removes a temporary file or directory. A failure is logged instead of thrown, so cleanup
    /// never replaces the result of the operation, while a secret left behind stays visible.
    /// </summary>
    internal static void TryDelete(string? path, ILogger? logger = null)
    {
        if (path is null)
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "The temporary protocol secret {Path} could not be deleted", path);
        }
    }

    /// <summary>
    /// Removes the entries a crashed process left behind, which can hold SSH keys or passwords.
    /// Only safe before this process opens sessions; other containers have their own temp directory.
    /// </summary>
    public static void DeleteOrphans(IEnumerable<string?> roots, ILogger? logger = null)
    {
        foreach (string root in roots.OfType<string>())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string entry in Directory.GetFileSystemEntries(root, NamePrefix + "*"))
                    TryDelete(entry, logger);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(exception, "The temporary directory {Path} could not be cleaned", root);
            }
        }
    }

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
