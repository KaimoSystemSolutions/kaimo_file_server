using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Testable wrapper that exposes the internal FileSystemStorage
/// constructor without IServiceProvider (no DB dependency).
/// This uses reflection since FileSystemStorage may be internal.
/// </summary>
public class FileSystemStorageTestable : Kaimo_File_Server.Core.Storage.IStorageEngine
{
    private readonly object _inner;
    private readonly Type _type;
    private readonly string _rootPath;

    public FileSystemStorageTestable(string rootPath)
    {
        this._rootPath = rootPath;
        
        _type = typeof(Kaimo_File_Server.Infrastructure.ServiceCollectionExtensions)
            .Assembly
            .GetType("Kaimo_File_Server.Infrastructure.Storage.FileSystemStorage")!;

        // Use the constructor that takes (string, Guid, IServiceProvider?)
        // Pass Guid.Empty and null to test without a database
        _inner = Activator.CreateInstance(_type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public,
            null, [rootPath, Guid.Empty, (IServiceProvider?)null], null)!;
    }

    public Task<Stream> ReadAsync(string path)
        => Invoke<Stream>("ReadAsync", path);

    public Task<Stream> ReadAsync(string path, bool preserveAccessTime)
        => Invoke<Stream>("ReadAsync", path, preserveAccessTime);

    public Task WriteAsync(string path, Stream data, CancellationToken cancellationToken = default)
        => Invoke("WriteAsync", path, data, cancellationToken);

    public Task CreateDirectory(string path)
        => Invoke("CreateDirectory", path);

    public Task DeleteAsync(string path)
        => Invoke("DeleteAsync", path);

    public Task<FileMetadata> GetMetadataAsync(string path)
        => Invoke<FileMetadata>("GetMetadataAsync", path);

    public Task CreateDirectoryAsync(string path)
        => Invoke("CreateDirectoryAsync", path);

    public Task<bool> IsDirectoryAsync(string path)
        => Invoke<bool>("IsDirectoryAsync", path);

    public Task<List<FileMetadata>> ListAsync(string directoryPath)
        => Invoke<List<FileMetadata>>("ListAsync", directoryPath);

    public Task<string> MoveAsync(string oldPath, string newPath)
        => Invoke<string>("MoveAsync", oldPath, newPath);

    public Task RenameFileAsync(string oldPath, string newPath)
        => Invoke("RenameFileAsync", oldPath, newPath);

    public Task RenameDirectoryAsync(string oldDirPath, string newDirPath)
        => Invoke("RenameDirectoryAsync", oldDirPath, newDirPath);

    public Task UnzipAsync(string zipPath, string targetPath)
        => Invoke("UnzipAsync", zipPath, targetPath);

    public Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format)
        => Invoke("ArchiveAsync", sourcePaths, targetPath, format);

    public Task<long> GetDirectorySizeAsync(string relativePath)
        => Invoke<long>("GetDirectorySizeAsync", relativePath);

    public Task<bool> ExistsAsync(string path)
        => Invoke<bool>("ExistsAsync", path);

    public Task<Kaimo_File_Server.Core.Storage.IStorageHandle> OpenAsync(
        string path,
        Kaimo_File_Server.Core.Storage.OpenMode mode,
        Kaimo_File_Server.Core.Storage.AccessIntent intent,
        Kaimo_File_Server.Core.Storage.ShareIntent share,
        CancellationToken ct = default)
        => Invoke<Kaimo_File_Server.Core.Storage.IStorageHandle>(
            "OpenAsync", path, mode, intent, share, ct);


    public Task SetModifiedDateAsync(string path, DateTime time)
        => Invoke("SetModifiedDateAsync", path, time);
    public Task SetCreatedDateAsync(string path, DateTime time)
        => Invoke("SetCreatedDateAsync", path, time);
    public Task SetAccessedDateAsync(string path, DateTime time)
        => Invoke("SetAccessedDateAsync", path, time);

    
    // -- Helpers that unwrap TargetInvocationException from reflection --

    // Overloads (e.g. ReadAsync) make a lookup by name alone ambiguous; match the argument count.
    private System.Reflection.MethodInfo FindMethod(string method, object[] args)
        => _type.GetMethods().Single(m => m.Name == method && m.GetParameters().Length == args.Length);

    private async Task Invoke(string method, params object[] args)
    {
        try
        {
            await (Task)FindMethod(method, args).Invoke(_inner, args)!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private async Task<T> Invoke<T>(string method, params object[] args)
    {
        try
        {
            return await (Task<T>)FindMethod(method, args).Invoke(_inner, args)!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    public string ToAbsolutePath(string shareRelativePath)
    {
        throw new NotImplementedException();
    }
}
