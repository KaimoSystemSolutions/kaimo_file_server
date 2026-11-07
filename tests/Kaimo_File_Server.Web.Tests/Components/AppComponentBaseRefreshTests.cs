using Bunit;
using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Components;

/// <summary>
/// Pins <see cref="AppComponentBase"/>'s background refresh: a reported change re-renders, a gone
/// component or circuit is ignored quietly, and any other failure is logged instead of vanishing.
/// </summary>
public sealed class AppComponentBaseRefreshTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly CapturingProvider _logs = new();

    public AppComponentBaseRefreshTests()
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(MailNotificationTests.FindWebRoot());
        _ctx.Services.AddSingleton(new AssetProvider(env.Object));
        _ctx.Services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs]));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Refresh_ReportsChange_Rerenders()
    {
        var cut = _ctx.Render<RefreshProbe>();
        var before = cut.Instance.Renders;

        cut.InvokeAsync(() => cut.Instance.Run(() => Task.FromResult(true)));

        cut.WaitForAssertion(() => Assert.True(cut.Instance.Renders > before));
    }

    [Fact]
    public void Refresh_UnexpectedFailure_IsLoggedAsError()
    {
        var cut = _ctx.Render<RefreshProbe>();

        cut.InvokeAsync(() => cut.Instance.Run(() => throw new InvalidOperationException("boom")));

        cut.WaitForAssertion(() => Assert.Contains(_logs.Entries,
            e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException));
    }

    [Fact]
    public void Refresh_ComponentGone_IsIgnoredQuietly()
    {
        var cut = _ctx.Render<RefreshProbe>();

        cut.InvokeAsync(() => cut.Instance.Run(() => throw new ObjectDisposedException("renderer")));

        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    private sealed class RefreshProbe : AppComponentBase
    {
        public int Renders;
        protected override void BuildRenderTree(RenderTreeBuilder builder) => Renders++;
        public void Run(Func<Task<bool>> refresh) => RefreshInBackground(refresh);
    }

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
        }
        public void Dispose() { }
    }
}
