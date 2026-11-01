using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for <see cref="SearchIndexingService.SelectInitialEngineAsync"/>: on the very first start
/// (no search state stored, no file changes logged yet) Elasticsearch is kept when it answers within
/// the grace window, otherwise the local PostgreSQL index is selected. An existing installation and
/// an engine the admin picks meanwhile are never touched.
/// </summary>
public sealed class SearchInitialEngineTests
{
    private static readonly SearchEngineState EsReachable = new(true, true, true, SearchEngine.Elasticsearch);
    private static readonly SearchEngineState EsDown = new(true, false, false, SearchEngine.Elasticsearch);

    private static (SearchIndexingService service, Mock<ISearchAdminService> admin, Mock<ISearchConfigStore> store)
        Setup(bool engineStored, long changeLogHead = 0, DemoModeOptions? demo = null)
    {
        var admin = new Mock<ISearchAdminService>();
        admin.Setup(a => a.SetEngineAsync(It.IsAny<SearchEngine>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var store = new Mock<ISearchConfigStore>();
        store.Setup(s => s.HasSearchStateAsync()).ReturnsAsync(engineStored);
        var changeLog = new Mock<IFileChangeLogRepository>();
        changeLog.Setup(l => l.GetHeadSeqGlobalAsync(It.IsAny<CancellationToken>())).ReturnsAsync(changeLogHead);
        var scopes = new ServiceCollection().AddSingleton(changeLog.Object).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var service = new SearchIndexingService(
            scopes, Mock.Of<IServiceProvider>(), Mock.Of<ISearchService>(),
            admin.Object, store.Object, NullLogger<SearchIndexingService>.Instance, demo);
        return (service, admin, store);
    }

    private static void VerifyNothingStored(Mock<ISearchAdminService> admin)
        => admin.Verify(a => a.SetEngineAsync(It.IsAny<SearchEngine>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task EngineAlreadyStored_LeavesItAlone()
    {
        var (service, admin, _) = Setup(engineStored: true);

        await service.SelectInitialEngineAsync(TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.GetStateAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyNothingStored(admin);
    }

    [Fact]
    public async Task ExistingInstallWithFileChanges_LeavesItAlone()
    {
        // An install that ran without Elasticsearch for a while stored no search key at all, but
        // its change log shows it is not new: it keeps its behavior instead of a surprise rebuild.
        var (service, admin, _) = Setup(engineStored: false, changeLogHead: 42);
        admin.Setup(a => a.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EsDown);

        await service.SelectInitialEngineAsync(TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.GetStateAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyNothingStored(admin);
    }

    [Fact]
    public async Task ReadOnlyDemo_SkipsSelection()
    {
        // The demo can never persist a choice; waiting for Elasticsearch would only delay indexing.
        var (service, admin, _) = Setup(engineStored: false, demo: new DemoModeOptions { ReadOnly = true });

        await service.SelectInitialEngineAsync(TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.GetStateAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyNothingStored(admin);
    }

    [Fact]
    public async Task FirstStart_EsReachable_PersistsElasticsearch()
    {
        var (service, admin, _) = Setup(engineStored: false);
        admin.Setup(a => a.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EsReachable);

        await service.SelectInitialEngineAsync(TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.SetEngineAsync(SearchEngine.Elasticsearch, It.IsAny<CancellationToken>()), Times.Once);
        admin.Verify(a => a.SetEngineAsync(SearchEngine.Local, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstStart_EsNeverReachable_SwitchesToLocal()
    {
        var (service, admin, _) = Setup(engineStored: false);
        admin.Setup(a => a.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EsDown);

        await service.SelectInitialEngineAsync(TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.SetEngineAsync(SearchEngine.Local, It.IsAny<CancellationToken>()), Times.Once);
        admin.Verify(a => a.SetEngineAsync(SearchEngine.Elasticsearch, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstStart_EsComesUpWithinGrace_KeepsElasticsearch()
    {
        // A fresh compose stack starts the web container before Elasticsearch is up.
        var (service, admin, _) = Setup(engineStored: false);
        admin.SetupSequence(a => a.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EsDown)
            .ReturnsAsync(EsDown)
            .ReturnsAsync(EsReachable);

        await service.SelectInitialEngineAsync(TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        admin.Verify(a => a.GetStateAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
        admin.Verify(a => a.SetEngineAsync(SearchEngine.Elasticsearch, It.IsAny<CancellationToken>()), Times.Once);
        admin.Verify(a => a.SetEngineAsync(SearchEngine.Local, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdminSelectsEngineDuringWait_IsNotOverwritten()
    {
        // The admin picks an engine in Settings while Elasticsearch is still starting. Afterwards
        // the selected engine answers (Local is always reachable), which must not be mistaken for
        // Elasticsearch coming up.
        var (service, admin, store) = Setup(engineStored: false);
        store.SetupSequence(s => s.HasSearchStateAsync())
            .ReturnsAsync(false)
            .ReturnsAsync(true);
        admin.SetupSequence(a => a.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EsDown)
            .ReturnsAsync(new SearchEngineState(true, true, true, SearchEngine.Local));

        await service.SelectInitialEngineAsync(TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        VerifyNothingStored(admin);
    }

    [Fact]
    public async Task StoreUnreadable_DoesNotThrowOrSwitch()
    {
        var (service, admin, store) = Setup(engineStored: false);
        store.Setup(s => s.HasSearchStateAsync()).ThrowsAsync(new InvalidOperationException("db down"));

        await service.SelectInitialEngineAsync(TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);

        VerifyNothingStored(admin);
    }

    [Fact]
    public async Task PersistFails_DoesNotThrow()
    {
        var (service, admin, _) = Setup(engineStored: false);
        admin.Setup(a => a.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EsDown);
        admin.Setup(a => a.SetEngineAsync(It.IsAny<SearchEngine>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db write failed"));

        await service.SelectInitialEngineAsync(TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);
    }
}
