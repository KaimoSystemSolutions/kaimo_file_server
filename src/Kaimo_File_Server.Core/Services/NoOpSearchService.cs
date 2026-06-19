using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Search;

namespace Kaimo_File_Server.Core.Services;

public class NoOpSearchService : ISearchService
{
    public Task onFileCreated(string absolutePath, Task<Stream> fileData, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task onFileDeleted(string absolutePath)
        => Task.CompletedTask;
    public Task onDirectoryCreated(string absolutePath)
        => Task.CompletedTask;
    public Task onDirectoryDeleted(string absolutePath)
        => Task.CompletedTask;
    public Task onFileRenamed(string oldAbsolutePath, string newAbsolutePath)
        => Task.CompletedTask;
    public Task onDirectoryRenamed(string oldAbsolutePath, string newAbsolutePath)
        => Task.CompletedTask;
    public Task<List<FileDocument>> SearchAsync(string searchText, UserContext user, CancellationToken ct = default)
        => Task.FromResult(new List<FileDocument>());
    public Task InitializeAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
