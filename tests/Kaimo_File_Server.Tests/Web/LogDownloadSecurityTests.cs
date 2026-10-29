using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Retained service logs contain user names, client addresses and internal details. A log
/// download link is a capability URL that can leak through browser history or a proxy log,
/// so it is bound to the user who requested it, works once, expires after two minutes, and
/// only while that user is still enabled and still holds
/// <see cref="ManagementPermission.ViewSystemLogs"/> — never on a public read-only demo.
/// </summary>
public sealed class LogDownloadSecurityTests
{
    private static readonly LogArchiveQuery Query =
        new(["web"], SearchText: "error", UtcDate: new DateOnly(2026, 10, 29));

    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly MutableTimeProvider _time = new() { Now = DateTimeOffset.UtcNow };
    private readonly LogDownloadTokenService _tokens;
    private readonly Mock<ILogArchiveReader> _reader = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "hash", "nt");

    public LogDownloadSecurityTests()
    {
        _tokens = new LogDownloadTokenService(_protection, _time);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(_admin);
        _contexts.Setup(c => c.CreateAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => new UserContext(u, [], [], new HashSet<string>()));
        GrantLogPermission(true);
    }

    // ── Token ──

    [Fact]
    public void Token_RoundTripsQueryAndUser_AndIsSingleUse()
    {
        var token = _tokens.Protect(Query, _admin.Id);

        Assert.True(_tokens.TryConsume(token, out var query, out var userId));
        Assert.Equal(_admin.Id, userId);
        Assert.Equal(["web"], query.Sources);
        Assert.Equal("error", query.SearchText);
        Assert.Equal(new DateOnly(2026, 10, 29), query.UtcDate);
        Assert.False(_tokens.TryConsume(token, out _, out _));
    }

    [Fact]
    public void Token_EachIssueIsIndependent()
    {
        var first = _tokens.Protect(Query, _admin.Id);
        var second = _tokens.Protect(Query, _admin.Id);

        Assert.NotEqual(first, second);
        Assert.True(_tokens.TryConsume(first, out _, out _));
        Assert.True(_tokens.TryConsume(second, out _, out _));
    }

    [Fact]
    public void Token_StillValidJustBeforeExpiry()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        _time.Advance(LogDownloadTokenService.Lifetime - TimeSpan.FromSeconds(1));

        Assert.True(_tokens.TryConsume(token, out _, out _));
    }

    [Fact]
    public void Token_ExpiredIsRejected()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        _time.Advance(LogDownloadTokenService.Lifetime + TimeSpan.FromSeconds(1));

        Assert.False(_tokens.TryConsume(token, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Token_GarbageIsRejected(string token)
        => Assert.False(_tokens.TryConsume(token, out _, out _));

    [Fact]
    public void Token_TamperedIsRejected()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        var tampered = token[..^4] + (token[^4] == 'A' ? 'B' : 'A') + token[^3..];

        Assert.False(_tokens.TryConsume(tampered, out _, out _));
    }

    [Fact]
    public void Token_FromOtherKeyIsRejected()
    {
        var foreign = new LogDownloadTokenService(new EphemeralDataProtectionProvider());

        Assert.False(_tokens.TryConsume(foreign.Protect(Query, _admin.Id), out _, out _));
    }

    [Fact]
    public void Token_FromPreviousUnboundFormatIsRejected()
    {
        // v1 tokens carried only the query; they must not be accepted after the upgrade.
        var legacy = _protection
            .CreateProtector("Kaimo.FileServer.SystemLogs.Download.v1")
            .ToTimeLimitedDataProtector()
            .Protect("""{"sources":["web"],"limit":1000}""", TimeSpan.FromMinutes(2));

        Assert.False(_tokens.TryConsume(legacy, out _, out _));
    }

    [Fact]
    public void Token_KeepsQuerySanitization()
    {
        var query = new LogArchiveQuery(["web"], SearchText: new string('x', 300), Limit: 50);

        Assert.True(_tokens.TryConsume(_tokens.Protect(query, _admin.Id), out var restored, out _));
        Assert.Equal(200, restored.SearchText!.Length);
        Assert.Equal(1000, restored.Limit);
        // Without a selected day the download is scoped to today (UTC).
        Assert.Equal(DateOnly.FromDateTime(_time.Now.UtcDateTime), restored.UtcDate);
    }

    // ── Controller ──

    [Fact]
    public async Task Download_ValidToken_StreamsLogs()
    {
        var controller = Controller();

        var result = await controller.Download(_tokens.Protect(Query, _admin.Id), "log", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        _reader.Verify(r => r.WriteDownloadAsync(
            It.Is<LogArchiveQuery>(q => q.SearchText == "error"), It.IsAny<Stream>(), true,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Download_ReplayedToken_IsUnauthorized()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        Assert.IsType<EmptyResult>(await Controller().Download(token, null, CancellationToken.None));

        Assert.IsType<UnauthorizedResult>(await Controller().Download(token, null, CancellationToken.None));
        VerifyStreamed(Times.Once());
    }

    [Fact]
    public async Task Download_ExpiredToken_IsUnauthorized()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        _time.Advance(LogDownloadTokenService.Lifetime + TimeSpan.FromSeconds(1));

        Assert.IsType<UnauthorizedResult>(await Controller().Download(token, null, CancellationToken.None));
        VerifyStreamed(Times.Never());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task Download_InvalidToken_IsUnauthorized(string token)
    {
        Assert.IsType<UnauthorizedResult>(await Controller().Download(token, null, CancellationToken.None));
        VerifyStreamed(Times.Never());
    }

    [Fact]
    public async Task Download_PermissionRevokedAfterIssue_IsForbidden()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        GrantLogPermission(false);

        var result = Assert.IsType<StatusCodeResult>(await Controller().Download(token, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        VerifyStreamed(Times.Never());
    }

    [Fact]
    public async Task Download_UserDisabledAfterIssue_IsForbidden()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(
            new User(_admin.Id, "Admin", "admin", "hash", "nt", isEnabled: false));

        var result = Assert.IsType<StatusCodeResult>(await Controller().Download(token, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        VerifyStreamed(Times.Never());
    }

    [Fact]
    public async Task Download_UserDeletedAfterIssue_IsUnauthorized()
    {
        var token = _tokens.Protect(Query, _admin.Id);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync((User?)null);

        Assert.IsType<UnauthorizedResult>(await Controller().Download(token, null, CancellationToken.None));
        VerifyStreamed(Times.Never());
    }

    [Fact]
    public async Task Download_InDemoMode_IsForbiddenEvenWithValidToken()
    {
        var result = await Controller(demo: true).Download(
            _tokens.Protect(Query, _admin.Id), null, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        VerifyStreamed(Times.Never());
    }

    // ── View model ──

    [Fact]
    public async Task ViewModel_DownloadUrl_IsBoundToTheSignedInUser()
    {
        var viewModel = ViewModel("admin");
        await viewModel.InitializeAsync();

        var url = viewModel.CreateDownloadUrl("https://files.example.test/", "log");

        var token = Uri.UnescapeDataString(url.Split("token=")[1].Split('&')[0]);
        Assert.True(_tokens.TryConsume(token, out _, out var userId));
        Assert.Equal(_admin.Id, userId);
    }

    [Fact]
    public async Task ViewModel_WithoutPermission_IssuesNoUrl()
    {
        GrantLogPermission(false);
        var viewModel = ViewModel("admin");
        await viewModel.InitializeAsync();

        Assert.Equal("", viewModel.CreateDownloadUrl("https://files.example.test/", "log"));
    }

    // ── helpers ──

    private void GrantLogPermission(bool granted)
        => _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ViewSystemLogs))
            .ReturnsAsync(granted);

    private void VerifyStreamed(Times times)
        => _reader.Verify(r => r.WriteDownloadAsync(
            It.IsAny<LogArchiveQuery>(), It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), times);

    private LogDownloadController Controller(bool demo = false)
        => new(_tokens, _reader.Object, _users.Object, _contexts.Object, _auth.Object,
            new DemoModeOptions { ReadOnly = demo })
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private LogViewerViewModel ViewModel(string username)
    {
        _contexts.Setup(c => c.CreateByUsernameAsync(username))
            .ReturnsAsync(new UserContext(_admin, [], [], new HashSet<string>()));
        _reader.Setup(r => r.GetSourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["web"]);
        _reader.Setup(r => r.QueryAsync(It.IsAny<LogArchiveQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogArchiveQueryResult([], false));

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test"));
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));

        return new LogViewerViewModel(
            _reader.Object, _tokens, authState.Object, _contexts.Object, _auth.Object,
            NullLogger<LogViewerViewModel>.Instance);
    }
}
