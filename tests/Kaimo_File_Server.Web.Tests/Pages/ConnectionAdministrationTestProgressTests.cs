using Bunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Ssh;
using Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;
using Kaimo_File_Server.Web.Services.Ui;
using Kaimo_File_Server.Web.Tests.Services;
using Kaimo_File_Server.Web.Tests.ViewModels.CloudAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>
/// A connection test belongs to the connection it was started on: the progress bar is shown only
/// there, and finishing the test neither changes the operator's current selection nor puts a
/// failure banner on another connection.
/// </summary>
public sealed class ConnectionAdministrationTestProgressTests
{
    private const string ProgressSelector = ".connection-test-progress";
    private const string FailureBannerSelector = ".detail-alerts .error-banner";

    [Fact]
    public async Task Testing_SelectedConnection_ShowsProgress()
    {
        await using var scenario = Scenario.Start();
        var cut = scenario.Render();

        Select(cut, "Alpha");
        StartTest(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(ProgressSelector)));
    }

    [Fact]
    public async Task SelectingOtherConnectionDuringTest_HidesProgressUntilTestedConnectionIsReselected()
    {
        await using var scenario = Scenario.Start();
        var cut = scenario.Render();
        Select(cut, "Alpha");
        StartTest(cut);

        Select(cut, "Beta");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Beta", DetailTitle(cut));
            Assert.Empty(cut.FindAll(ProgressSelector));
        });

        Select(cut, "Alpha");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(ProgressSelector)));
    }

    [Fact]
    public async Task TestFinishingWhileOtherConnectionSelected_KeepsCurrentSelection()
    {
        await using var scenario = Scenario.Start();
        var cut = scenario.Render();
        Select(cut, "Alpha");
        StartTest(cut);
        Select(cut, "Beta");

        scenario.Finish(StorageConnectionHealthState.Healthy, "ok");

        cut.WaitForAssertion(() => Assert.True(scenario.Alpha.LastVerifiedAtUtc is not null));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Beta", DetailTitle(cut));
            Assert.Empty(cut.FindAll(ProgressSelector));
        });
    }

    [Fact]
    public async Task TestFailingWhileOtherConnectionSelected_ShowsNoFailureBannerOnIt()
    {
        await using var scenario = Scenario.Start();
        var cut = scenario.Render();
        Select(cut, "Alpha");
        StartTest(cut);
        Select(cut, "Beta");

        scenario.Finish(StorageConnectionHealthState.Unavailable, "smb_connection_failed");

        cut.WaitForAssertion(() => Assert.Equal("smb_connection_failed", scenario.Alpha.LastErrorCode));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Beta", DetailTitle(cut));
            Assert.Empty(cut.FindAll(FailureBannerSelector));
        });
    }

    [Fact]
    public async Task TestFailingOnSelectedConnection_ShowsFailureBanner()
    {
        await using var scenario = Scenario.Start();
        var cut = scenario.Render();
        Select(cut, "Alpha");
        StartTest(cut);

        scenario.Finish(StorageConnectionHealthState.Unavailable, "smb_connection_failed");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Alpha", DetailTitle(cut));
            Assert.Empty(cut.FindAll(ProgressSelector));
            Assert.NotEmpty(cut.FindAll(FailureBannerSelector));
        });
    }

    private static void Select(IRenderedComponent<ConnectionAdministration> cut, string name)
    {
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button.connection-row"),
            row => row.TextContent.Contains(name, StringComparison.Ordinal)));
        cut.FindAll("button.connection-row")
            .First(row => row.TextContent.Contains(name, StringComparison.Ordinal))
            .Click();
    }

    private static void StartTest(IRenderedComponent<ConnectionAdministration> cut)
        => cut.Find("button.btn-tinted--info").Click();

    private static string DetailTitle(IRenderedComponent<ConnectionAdministration> cut)
        => cut.Find(".detail-head-title").TextContent.Trim();

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly BunitContext _ctx = new();
        private readonly TaskCompletionSource<StorageConnectionHealthResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StorageConnection Alpha { get; } = CloudAccessViewModelTestFactory.SmbConnection("Alpha");
        public StorageConnection Beta { get; } = CloudAccessViewModelTestFactory.SmbConnection("Beta");

        public static Scenario Start() => new();

        private Scenario()
        {
            var (viewModel, provider, _) = CloudAccessViewModelTestFactory.Create(Alpha, Beta);
            provider.Setup(x => x.TestAsync(It.IsAny<StorageConnection>(), It.IsAny<CancellationToken>()))
                .Returns(_result.Task);
            var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());

            _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            _ctx.Services.AddSingleton(new AssetProvider(env.Object));
            _ctx.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            _ctx.Services.AddSingleton(Mock.Of<IConfigRepository>());
            _ctx.Services.AddSingleton<DateFormatService>();
            _ctx.Services.AddSingleton<ToastService>();
            _ctx.Services.AddSingleton(Mock.Of<IRsyncSshSetupService>());
            _ctx.Services.AddSingleton<IStorageConnectionProviderCatalog>(
                new StorageConnectionProviderCatalog([provider.Object]));
            _ctx.Services.AddSingleton(viewModel);
        }

        public IRenderedComponent<ConnectionAdministration> Render() => _ctx.Render<ConnectionAdministration>();

        public void Finish(StorageConnectionHealthState state, string code)
            => _result.SetResult(new StorageConnectionHealthResult(state, code, DateTime.UtcNow));

        public ValueTask DisposeAsync() => _ctx.DisposeAsync();
    }
}
