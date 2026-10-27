using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Undo in the web file browser: each successful operation records its inverse, Ctrl+Z
/// replays the inverses of the last entry (LIFO), batches undo as one entry, and anything
/// that cannot be reverted without data loss is never recorded. The history is per folder.
/// </summary>
public sealed partial class FileBrowserViewModelUndoTests : IDisposable
{
    // Copy targets are probed on disk, so share paths map into this temp folder.
    private readonly string _root = Directory.CreateTempSubdirectory("kaimo-undo-").FullName;

    private readonly Mock<IFileServiceFactory> _fileServiceFactory = new();
    private readonly Mock<IFileService> _fileService = new();
    private readonly Mock<IShareRepository> _shareRepo = new();
    private readonly Mock<IUserContextFactory> _userContextFactory = new();
    private readonly Mock<AuthenticationStateProvider> _authState = new();
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();

    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true) { IsRecycleEnabled = true };
    private readonly ShareDefinition _otherShare = new("other", "/data/other", isEnabled: true) { IsRecycleEnabled = true };
    private readonly FileBrowserViewModel _sut;

    public FileBrowserViewModelUndoTests()
    {
        _shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);
        _shareRepo.Setup(r => r.GetByNameAsync("other")).ReturnsAsync(_otherShare);
        _fileServiceFactory
            .Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>()))
            .Returns(_fileService.Object);
        _fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync([]);
        _fileService
            .Setup(s => s.ReadFileAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        _fileService
            .Setup(s => s.ToAbsolutePath(It.IsAny<string>()))
            .Returns<string>(Abs);
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync(It.IsAny<string>(), It.IsAny<UserContext>(), true))
            .ReturnsAsync((string path, UserContext _, bool _) => ".RECYCLE_BIN/" + path);
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync(It.IsAny<string>(), It.IsAny<UserContext>(), false))
            .ReturnsAsync((string?)null);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, authenticationType: "test");
        _authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        _userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []));

        _syncRepo
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SyncDefinitionAdminEntry>());
        var shareLinkRepo = new Mock<IShareLinkRepository>();
        shareLinkRepo
            .Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ShareLink>());

        _sut = new FileBrowserViewModel(
            _fileServiceFactory.Object, _shareRepo.Object, new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            _userContextFactory.Object, new Mock<IManagementAuthService>().Object, _authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, new Mock<ISearchService>().Object, new Mock<IUserRepository>().Object,
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(), _syncRepo.Object,
            shareLinkRepo.Object);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Abs(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    private static FileMetadata Item(string path, bool isDirectory = false)
        => new() { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], IsDirectory = isDirectory, Acl = [] };

    private void VerifyRenamed(string from, string to, Times times)
        => _fileService.Verify(s => s.RenameAsync(from, to, It.IsAny<UserContext>()), times);

    private void VerifyRecycled(string path, Times times)
        => _fileService.Verify(s => s.DeleteFileAsync(path, It.IsAny<UserContext>(), true), times);

    // ═══════════════════ Nothing to undo ═══════════════════

    [Fact]
    public async Task CanUndo_IsFalse_BeforeAnyOperation()
    {
        await _sut.LoadShareAsync("share", "docs");

        Assert.False(_sut.CanUndo);
        Assert.False((await _sut.UndoAsync()).Success);
    }

    // ═══════════════════ Rename / move ═══════════════════

    [Fact]
    public async Task Rename_Undo_RenamesBack()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        Assert.True(_sut.CanUndo);
        var result = await _sut.UndoAsync();

        Assert.True(result.Success);
        VerifyRenamed("docs/b.txt", "docs/a.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task FailedRename_IsNotRecorded()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(s => s.RenameAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserContext>()))
            .ThrowsAsync(new IOException("Target already exists"));

        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task Move_Undo_MovesBack()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.MoveAsync(Item("docs/a.txt"), "docs/sub");

        await _sut.UndoAsync();

        VerifyRenamed("docs/sub/a.txt", "docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task NoOpMove_IsNotRecorded()
    {
        await _sut.LoadShareAsync("share", "docs");

        await _sut.MoveAsync(Item("docs/a.txt"), "docs");

        Assert.False(_sut.CanUndo);
    }

    // ═══════════════════ Created items ═══════════════════

    [Fact]
    public async Task CreateFolder_Undo_RecyclesTheFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.CreateFolderAsync("New");

        await _sut.UndoAsync();

        VerifyRecycled("docs/New", Times.Once());
    }

    [Fact]
    public async Task CreateFolder_WithoutRecycleBin_IsNotUndoable()
    {
        _share.IsRecycleEnabled = false;
        await _sut.LoadShareAsync("share", "docs");

        await _sut.CreateFolderAsync("New");

        // Undo must never delete permanently.
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task Archive_Undo_RecyclesTheArchive()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.ArchiveAsync([Item("docs/a.txt")], ".zip");

        await _sut.UndoAsync();

        VerifyRecycled("docs/a.zip", Times.Once());
    }

    [Fact]
    public async Task Unzip_Undo_RecyclesTheExtractedFolder()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.UnzipAsync(Item("docs/pack.zip"));

        await _sut.UndoAsync();

        VerifyRecycled("docs/pack", Times.Once());
    }

    // ═══════════════════ Delete ═══════════════════

    [Fact]
    public async Task Delete_Undo_RestoresFromTheRecyclePathThatLanded()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync("docs/a.txt", It.IsAny<UserContext>(), true))
            .ReturnsAsync(".RECYCLE_BIN/docs/a.txt_2026-10-25_10-00-00");
        await _sut.DeleteAsync(Item("docs/a.txt"));

        await _sut.UndoAsync();

        VerifyRenamed(".RECYCLE_BIN/docs/a.txt_2026-10-25_10-00-00", "docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task Delete_WithoutRecycleBin_IsNotUndoable()
    {
        _share.IsRecycleEnabled = false;
        await _sut.LoadShareAsync("share", "docs");

        var result = await _sut.DeleteAsync(Item("docs/a.txt"));

        Assert.True(result.Success);
        Assert.False(_sut.CanUndo);
    }

    // ═══════════════════ Groups (batch actions) ═══════════════════

    [Fact]
    public async Task GroupedDeletes_UndoAsOneEntry()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.DeleteAsync(Item("docs/a.txt"));
            await _sut.DeleteAsync(Item("docs/b.txt"));
            await _sut.DeleteAsync(Item("docs/c.txt"));
        }

        await _sut.UndoAsync();

        VerifyRenamed(".RECYCLE_BIN/docs/a.txt", "docs/a.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/b.txt", "docs/b.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/c.txt", "docs/c.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task Group_WithOnePermanentDelete_IsDiscardedWhole()
    {
        await _sut.LoadShareAsync("share", "docs");
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync("docs/b.txt", It.IsAny<UserContext>(), true))
            .ReturnsAsync((string?)null);
        using (_sut.BeginUndoGroup())
        {
            await _sut.DeleteAsync(Item("docs/a.txt"));
            await _sut.DeleteAsync(Item("docs/b.txt"));
        }

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task EmptyGroup_RecordsNothing()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup()) { }

        Assert.False(_sut.CanUndo);
    }

    // ═══════════════════ Paste (copy / cut) ═══════════════════

    [Fact]
    public async Task PastedCopy_Undo_RecyclesOnlyNewTargets_AndSkipsChildrenOfCreatedFolders()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.CreateFolderAtAsync("docs/Photos");
            Directory.CreateDirectory(Abs("docs/Photos"));
            await _sut.CopyAsync(Item("src/Photos/p.jpg"), "docs/Photos/p.jpg", CancellationToken.None);
            await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);
        }

        await _sut.UndoAsync();

        VerifyRecycled("docs/Photos", Times.Once());
        VerifyRecycled("docs/Photos/p.jpg", Times.Never());
        VerifyRecycled("docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task PasteIntoExistingFolder_RecordsTheNewChildren_NotTheFolder()
    {
        Directory.CreateDirectory(Abs("docs/Photos"));
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.CreateFolderAtAsync("docs/Photos");
            await _sut.CopyAsync(Item("src/Photos/p.jpg"), "docs/Photos/p.jpg", CancellationToken.None);
        }

        await _sut.UndoAsync();

        VerifyRecycled("docs/Photos", Times.Never());
        VerifyRecycled("docs/Photos/p.jpg", Times.Once());
    }

    [Fact]
    public async Task PasteOverAnExistingFile_IsNotUndoable()
    {
        Directory.CreateDirectory(Abs("docs"));
        File.WriteAllText(Abs("docs/a.txt"), "old");
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.CopyAsync(Item("src/b.txt"), "docs/b.txt", CancellationToken.None);
            await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);
        }

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task CutPaste_Undo_RestoresSourcesThenRecyclesTargets()
    {
        await _sut.LoadShareAsync("share", "docs");
        var order = new List<string>();
        _fileService
            .Setup(s => s.RenameAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UserContext>()))
            .Callback<string, string, UserContext>((from, _, _) => order.Add("restore " + from))
            .Returns(Task.CompletedTask);
        _fileService
            .Setup(s => s.DeleteFileAsync(It.IsAny<string>(), It.IsAny<UserContext>(), true))
            .Callback<string, UserContext, bool>((path, _, _) => order.Add("recycle " + path))
            .Returns(Task.CompletedTask);
        using (_sut.BeginUndoGroup())
        {
            await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);
            await _sut.DeleteAsync(Item("src/a.txt"));
        }

        await _sut.UndoAsync();

        Assert.Equal(["restore .RECYCLE_BIN/src/a.txt", "recycle docs/a.txt"], order);
    }

    // ═══════════════════ Stack behavior ═══════════════════

    [Fact]
    public async Task Undo_IsLastInFirstOut()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");
        await _sut.RenameAsync(Item("docs/c.txt"), "d.txt");

        await _sut.UndoAsync();
        VerifyRenamed("docs/d.txt", "docs/c.txt", Times.Once());
        VerifyRenamed("docs/b.txt", "docs/a.txt", Times.Never());

        await _sut.UndoAsync();
        VerifyRenamed("docs/b.txt", "docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task Stack_KeepsOnlyTheLast20Entries()
    {
        await _sut.LoadShareAsync("share", "docs");
        for (var i = 0; i < 21; i++)
            await _sut.RenameAsync(Item($"docs/{i}.txt"), $"{i}-new.txt");

        for (var i = 0; i < 20; i++)
            await _sut.UndoAsync();

        Assert.False(_sut.CanUndo);
        VerifyRenamed("docs/0-new.txt", "docs/0.txt", Times.Never());
        VerifyRenamed("docs/1-new.txt", "docs/1.txt", Times.Once());
    }

    [Fact]
    public async Task FailedUndo_ReportsFailure_AndDropsTheEntry()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");
        _fileService
            .Setup(s => s.RenameAsync("docs/b.txt", "docs/a.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new IOException("Target already exists"));

        var result = await _sut.UndoAsync();

        Assert.False(result.Success);
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task FailedStep_DoesNotStopTheRemainingSteps()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.DeleteAsync(Item("docs/a.txt"));
            await _sut.DeleteAsync(Item("docs/b.txt"));
            await _sut.DeleteAsync(Item("docs/c.txt"));
        }
        _fileService
            .Setup(s => s.RenameAsync(".RECYCLE_BIN/docs/b.txt", "docs/b.txt", It.IsAny<UserContext>()))
            .ThrowsAsync(new IOException("Target already exists"));

        var result = await _sut.UndoAsync();

        Assert.False(result.Success);
        VerifyRenamed(".RECYCLE_BIN/docs/a.txt", "docs/a.txt", Times.Once());
        VerifyRenamed(".RECYCLE_BIN/docs/c.txt", "docs/c.txt", Times.Once());
        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task CanUndo_IsFalse_WhileAGroupIsOpen()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        using (_sut.BeginUndoGroup())
        {
            Assert.False(_sut.CanUndo);
            Assert.False((await _sut.UndoAsync()).Success);
        }

        VerifyRenamed("docs/b.txt", "docs/a.txt", Times.Never());
        Assert.True(_sut.CanUndo);
    }

    // ═══════════════════ Unrecorded mutations drop the history ═══════════════════

    [Fact]
    public async Task PermanentDelete_ClearsOlderEntries()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");
        _fileService
            .Setup(s => s.DeleteFileReturningRecyclePathAsync("docs/c.txt", It.IsAny<UserContext>(), true))
            .ReturnsAsync((string?)null);

        await _sut.DeleteAsync(Item("docs/c.txt"));

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task OverwritingPaste_ClearsOlderEntries()
    {
        Directory.CreateDirectory(Abs("docs"));
        File.WriteAllText(Abs("docs/a.txt"), "old");
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/x.txt"), "y.txt");

        using (_sut.BeginUndoGroup())
            await _sut.CopyAsync(Item("src/a.txt"), "docs/a.txt", CancellationToken.None);

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task Upload_ClearsTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        await _sut.UploadFileAsync("new.txt", new MemoryStream([1]));

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task RestoreVersion_ClearsTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        await _sut.RestoreVersionAsync(Item("docs/b.txt"), DateTime.UtcNow);

        Assert.False(_sut.CanUndo);
    }

    // ═══════════════════ Lifetime ═══════════════════

    [Fact]
    public async Task ReloadingTheSameFolder_KeepsTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        await _sut.LoadShareAsync("share", "docs");
        await _sut.RefreshCurrentDirectoryAsync();

        Assert.True(_sut.CanUndo);
    }

    [Fact]
    public async Task NavigatingToAnotherFolder_ClearsTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        await _sut.LoadShareAsync("share", "docs/sub");

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task SwitchingShares_ClearsTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        await _sut.LoadShareAsync("other", "docs");

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task OpenGroup_IsDiscarded_WhenNavigatingToAnotherShare()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.DeleteAsync(Item("docs/a.txt"));
            // A long batch keeps running while the user opens another share.
            await _sut.LoadShareAsync("other", "docs");
        }

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task OpenGroup_IsDiscarded_ByClearUndo()
    {
        await _sut.LoadShareAsync("share", "docs");
        using (_sut.BeginUndoGroup())
        {
            await _sut.DeleteAsync(Item("docs/a.txt"));
            _sut.ClearUndo();
            await _sut.DeleteAsync(Item("docs/b.txt"));
        }

        Assert.False(_sut.CanUndo);
    }

    [Fact]
    public async Task ClearUndo_EmptiesTheHistory()
    {
        await _sut.LoadShareAsync("share", "docs");
        await _sut.RenameAsync(Item("docs/a.txt"), "b.txt");

        _sut.ClearUndo();

        Assert.False(_sut.CanUndo);
    }
}
