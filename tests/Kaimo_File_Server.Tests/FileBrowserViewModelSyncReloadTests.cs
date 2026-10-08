using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
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
/// Pins the file browser's background sync-state reloads (triggered by the job runner) against
/// out-of-order completion: an older, slower reload must never overwrite newer state.
/// </summary>
public class FileBrowserViewModelSyncReloadTests
{
    private static readonly DateTime LastRun = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ShareDefinition _share = new("share", "/data/share", isEnabled: true);
    private readonly ShareDefinition _other = new("other", "/data/other", isEnabled: true);
    private readonly Mock<ISyncDefinitionRepository> _syncRepo = new();
    private readonly Mock<ICloudSyncJobRunner> _runner = new();
    private readonly FileBrowserViewModel _sut;

    // A file changed after the last success: synced only once a newer run has completed.
    private readonly FileMetadata _file = new() { Path = "a.txt", Name = "a.txt", ModifiedAt = LastRun.AddMinutes(5) };

    public FileBrowserViewModelSyncReloadTests()
    {
        var shareRepo = new Mock<IShareRepository>();
        shareRepo.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(_share);
        shareRepo.Setup(r => r.GetByNameAsync("other")).ReturnsAsync(_other);

        var fileService = new Mock<IFileService>();
        fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());
        var fileServiceFactory = new Mock<IFileServiceFactory>();
        fileServiceFactory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(fileService.Object);

        var authState = new Mock<AuthenticationStateProvider>();
        authState
            .Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], authenticationType: "test"))));
        var userContextFactory = new Mock<IUserContextFactory>();
        userContextFactory
            .Setup(f => f.CreateByUsernameAsync("alice"))
            .ReturnsAsync(new UserContext(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []));

        var shareLinkRepo = new Mock<IShareLinkRepository>();
        shareLinkRepo
            .Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ShareLink>());

        _runner.Setup(r => r.Jobs).Returns([]);

        _sut = new FileBrowserViewModel(
            fileServiceFactory.Object, shareRepo.Object, new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            userContextFactory.Object, new Mock<IManagementAuthService>().Object, authState.Object,
            NullLogger<FileBrowserViewModel>.Instance, new Mock<ISearchService>().Object,
            new Mock<IUserRepository>().Object, new FileDownloadTicketStore(), new ZipDownloadTicketStore(),
            new DemoModeOptions(), _syncRepo.Object, shareLinkRepo.Object, _runner.Object);
    }

    // A push sync over the whole share whose last success is the given time.
    private List<SyncDefinitionAdminEntry> Syncs(DateTime lastSuccess) =>
    [
        new(new SyncDefinition { LocalShareId = _share.Id, LocalPath = "", Mode = SyncMode.Push },
            new SyncDefinitionRuntime { LastSuccessfulRunAtUtc = lastSuccess })
    ];

    private void RaiseJobs(params SyncJobSnapshot[] jobs)
    {
        _runner.Setup(r => r.Jobs).Returns(jobs);
        _runner.Raise(r => r.OnChanged += null);
    }

    // Lets a completed repository task resume its reload inline, so the asserts see the result.
    private static void NoSyncContext() => SynchronizationContext.SetSynchronizationContext(null);

    private SyncJobSnapshot Job() => new(Guid.NewGuid(), "t", "d", 0, DateTimeOffset.UtcNow, false, _share.Id, "");

    [Fact]
    public async Task RunEndingWhileStartReloadIsPending_AppliesTheEndState()
    {
        NoSyncContext();
        var startReload = new TaskCompletionSource<List<SyncDefinitionAdminEntry>>();
        var endReload = new TaskCompletionSource<List<SyncDefinitionAdminEntry>>();
        _syncRepo.SetupSequence(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Syncs(LastRun))
            .Returns(startReload.Task)
            .Returns(endReload.Task);
        await _sut.LoadShareAsync("share");

        RaiseJobs(Job());  // run starts: reload reads the old last-success time ...
        RaiseJobs();       // ... and the run ends before that reload returns

        endReload.SetResult(Syncs(LastRun.AddMinutes(10)));
        startReload.SetResult(Syncs(LastRun)); // stale and older: must be discarded

        Assert.Equal(SyncItemState.Synced, _sut.GetSyncMarker(_file)!.State);
    }

    [Fact]
    public async Task ReloadFinishingAfterShareSwitch_DoesNotLeakIntoNewShare()
    {
        NoSyncContext();
        var reload = new TaskCompletionSource<List<SyncDefinitionAdminEntry>>();
        _syncRepo.SetupSequence(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Syncs(LastRun))
            .Returns(reload.Task)
            .ReturnsAsync([]);
        await _sut.LoadShareAsync("share");

        RaiseJobs(Job());
        await _sut.LoadShareAsync("other");
        reload.SetResult(Syncs(LastRun));

        Assert.Null(_sut.GetSyncMarker(_file));
    }
}
