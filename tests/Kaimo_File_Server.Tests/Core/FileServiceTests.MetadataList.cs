using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileServiceTests
{
    // ═══════════════════ GetMetadataAsync ═══════════════════

    [Fact]
    public async Task GetMetadataAsync_WithAccess_ReturnsMeta()
    {
        var ctx = CreateContext();
        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            Path = "test.txt",
            Name = "test.txt",
            Size = 100,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = []
        };
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        AllowAccess(FilePermission.ListReadData);

        Assert.Same(meta, await _sut.GetMetadataAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task GetMetadataAsync_WithoutAccess_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            Path = "test.txt",
            Name = "test.txt",
            Size = 0,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = []
        };
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(meta);
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("/test.txt", ctx));
    }

    [Fact]
    public async Task GetMetadataAsync_MissingPath_WithAccess_ThrowsFileNotFound()
    {
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ThrowsAsync(new FileNotFoundException());
        AllowAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.GetMetadataAsync("/missing.txt", CreateContext()));
    }

    [Fact]
    public async Task GetMetadataAsync_MissingPath_WithoutAccess_ThrowsUnauthorized()
    {
        // No existence oracle: a caller without list rights gets 403 whether or not the item exists.
        _storageMock.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ThrowsAsync(new FileNotFoundException());
        DenyAccess(FilePermission.ListReadData);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.GetMetadataAsync("/missing.txt", CreateContext()));
    }

    // ═══════════════════ ListAsync ═══════════════════

    [Fact]
    public async Task ListAsync_WithAccess_ReturnsVisibleItems()
    {
        var ctx = CreateContext();
        var items = new List<FileMetadata>
        {
            new() { Name = "visible.txt", IsDirectory = false, Path = "dir/visible.txt" },
            new() { Name = "hidden.txt", IsDirectory = false, Path = "dir/hidden.txt" },
        };
        _storageMock.Setup(s => s.ExistsAsync("dir")).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync("dir")).ReturnsAsync(true);
        _storageMock.Setup(s => s.ListAsync(It.IsAny<string>())).ReturnsAsync(items);

        // Allow listing the directory itself
        _aclMock
            .Setup(a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData))
            .ReturnsAsync(true);

        // Allow visible.txt, deny hidden.txt
        _aclMock
            .Setup(a => a.HasAccessBatchAsync(
                ctx, _shareId, It.IsAny<IReadOnlyList<(string, bool)>>(), FilePermission.ListReadData))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool IsDir)> entries, FilePermission _) =>
                entries.ToDictionary(e => e.Path, e => e.Path.Contains("visible")));

        var result = await _sut.ListAsync("dir", ctx);
        Assert.Single(result);
        Assert.Equal("visible.txt", result[0].Name);
    }

    [Fact]
    public async Task ListAsync_Root_HidesInternalNamespacesButKeepsRecycleBin()
    {
        var ctx = CreateContext();
        var items = new List<FileMetadata>
        {
            new()
            {
                Name = ".kaimo-close-captures",
                IsDirectory = true,
                Path = ".kaimo-close-captures"
            },
            new()
            {
                Name = ".KAIMO-temporary",
                IsDirectory = true,
                Path = ".KAIMO-temporary"
            },
            new()
            {
                Name = ".RECYCLE_BIN",
                IsDirectory = true,
                Path = ".RECYCLE_BIN"
            },
            new() { Name = "docs", IsDirectory = true, Path = "docs" }
        };
        _storageMock.Setup(s => s.ExistsAsync("")).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync("")).ReturnsAsync(true);
        _storageMock.Setup(s => s.ListAsync("")).ReturnsAsync(items);
        _aclMock.Setup(a => a.HasAccessAsync(
                ctx, _shareId, "", true, FilePermission.ListReadData))
            .ReturnsAsync(true);

        IReadOnlyList<(string Path, bool IsDirectory)>? checkedItems = null;
        _aclMock.Setup(a => a.HasAccessBatchAsync(
                ctx, _shareId,
                It.IsAny<IReadOnlyList<(string, bool)>>(),
                FilePermission.ListReadData))
            .Callback<UserContext, Guid,
                IReadOnlyList<(string Path, bool IsDirectory)>,
                FilePermission>((_, _, entries, _) => checkedItems = entries)
            .ReturnsAsync((UserContext _, Guid _,
                IReadOnlyList<(string Path, bool IsDirectory)> entries,
                FilePermission _) =>
                entries.ToDictionary(entry => entry.Path, _ => true));

        var result = await _sut.ListAsync("", ctx);

        Assert.Equal([".RECYCLE_BIN", "docs"],
            result.Select(item => item.Name));
        Assert.NotNull(checkedItems);
        Assert.Equal([".RECYCLE_BIN", "docs"],
            checkedItems.Select(item => item.Path));
    }

    [Fact]
    public async Task ListAsync_InternalNamespace_IsDeniedBeforeAclOrStorage()
    {
        var ctx = CreateContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.ListAsync(".kaimo-close-captures", ctx));

        _aclMock.Verify(a => a.HasAccessAsync(
            It.IsAny<UserContext>(), It.IsAny<Guid>(),
            It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<FilePermission>()), Times.Never);
        _storageMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListAsync_DirAccessDenied_ThrowsUnauthorized()
    {
        var ctx = CreateContext();
        _aclMock
            .Setup(a => a.HasAccessAsync(ctx, _shareId, It.IsAny<string>(), true, FilePermission.ListReadData))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ListAsync("dir", ctx));

        _storageMock.Verify(s => s.ExistsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ListAsync_NonExistentDirectory_ThrowsDirectoryNotFound()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.ListReadData);
        _storageMock.Setup(s => s.ExistsAsync("missing")).ReturnsAsync(false);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _sut.ListAsync("missing", ctx));

        _storageMock.Verify(s => s.ListAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ListAsync_FilePath_ThrowsDirectoryNotFound()
    {
        var ctx = CreateContext();
        AllowAccess(FilePermission.ListReadData);
        _storageMock.Setup(s => s.ExistsAsync("document.txt")).ReturnsAsync(true);
        _storageMock.Setup(s => s.IsDirectoryAsync("document.txt")).ReturnsAsync(false);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _sut.ListAsync("document.txt", ctx));

        _storageMock.Verify(s => s.ListAsync(It.IsAny<string>()), Times.Never);
    }
}
