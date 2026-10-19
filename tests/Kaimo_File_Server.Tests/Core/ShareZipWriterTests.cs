using System.IO.Compression;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.File;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="ShareZipWriter"/> backs multi-file and public-link ZIP downloads. Every read
/// goes through <see cref="IFileService"/>, so an entry the user cannot read (or that
/// vanished) must be skipped without aborting the archive or leaking its content.
/// </summary>
public sealed class ShareZipWriterTests
{
    private readonly Mock<IFileService> _fs = new();
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "U", "u", "h", "n"), [], [], []);

    private void AddFile(string path, string content)
    {
        _fs.Setup(f => f.GetMetadataAsync(path, _user)).ReturnsAsync(new FileMetadata { Path = path, Name = path.Split('/')[^1] });
        _fs.Setup(f => f.ReadFileAsync(path, _user)).ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private void AddDir(string path, params FileMetadata[] children)
    {
        _fs.Setup(f => f.GetMetadataAsync(path, _user)).ReturnsAsync(new FileMetadata { Path = path, Name = path.Split('/')[^1], IsDirectory = true });
        _fs.Setup(f => f.ListAsync(path, _user)).ReturnsAsync(children.ToList());
    }

    private static FileMetadata Child(string name, bool dir = false) => new() { Name = name, IsDirectory = dir };

    private async Task<Dictionary<string, string>> ZipAsync(params string[] paths)
    {
        using var output = new MemoryStream();
        await ShareZipWriter.WriteAsync(output, _fs.Object, paths, _user);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        var result = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = await reader.ReadToEndAsync();
        }
        return result;
    }

    [Fact]
    public async Task FileAndNestedFolder_UseRelativeEntryNames()
    {
        AddFile("docs/readme.txt", "hello");
        AddDir("docs/project", Child("a.txt"), Child("sub", dir: true));
        AddFile("docs/project/a.txt", "A");
        AddDir("docs/project/sub", Child("b.txt"));
        AddFile("docs/project/sub/b.txt", "B");

        var zip = await ZipAsync("/docs/readme.txt", "docs/project");

        Assert.Equal(new Dictionary<string, string>
        {
            ["readme.txt"] = "hello",
            ["project/a.txt"] = "A",
            ["project/sub/b.txt"] = "B",
        }, zip);
    }

    [Fact]
    public async Task UnreadableOrMissingEntries_AreSkipped()
    {
        AddDir("box", Child("ok.txt"), Child("denied.txt"), Child("locked", dir: true), Child("gone.txt"));
        AddFile("box/ok.txt", "ok");
        AddFile("box/denied.txt", "SECRET");
        _fs.Setup(f => f.ReadFileAsync("box/denied.txt", _user)).ThrowsAsync(new UnauthorizedAccessException());
        _fs.Setup(f => f.ListAsync("box/locked", _user)).ThrowsAsync(new UnauthorizedAccessException());
        _fs.Setup(f => f.ReadFileAsync("box/gone.txt", _user)).ThrowsAsync(new FileNotFoundException());
        _fs.Setup(f => f.GetMetadataAsync("hidden.txt", _user)).ThrowsAsync(new UnauthorizedAccessException());
        _fs.Setup(f => f.GetMetadataAsync("missing", _user)).ThrowsAsync(new DirectoryNotFoundException());

        var zip = await ZipAsync("box", "hidden.txt", "missing");

        Assert.Equal(["box/ok.txt"], zip.Keys);
        Assert.DoesNotContain(zip.Values, v => v.Contains("SECRET"));
    }

    [Fact]
    public async Task EmptyFolder_IsKeptAsDirectoryEntry()
    {
        AddDir("empty");

        var zip = await ZipAsync("empty");

        Assert.Equal(["empty/"], zip.Keys);
    }

    [Fact]
    public async Task RootOrBlankPath_IsIgnored()
    {
        var zip = await ZipAsync("", "/");

        Assert.Empty(zip);
        _fs.VerifyNoOtherCalls();
    }
}
