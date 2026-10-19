using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Security properties of the cookie-based web session: the cookie is HttpOnly/Secure/Strict,
/// it is honored on the Blazor hub only (never on the client API or WebDAV), the cookie-setting
/// endpoints accept same-origin HTTPS fetches only, and device tokens never act as web sessions.
/// </summary>
public class WebSessionCookieTests
{
    private const string Host = "files.example:8443";
    private const string OwnOrigin = "https://files.example:8443";

    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    private static HttpContext Request(string path = "/", string scheme = "https", string? origin = null,
        string? fetchSite = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(Host);
        context.Request.Path = path;
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return context;
    }

    // ─────────────── Scheme selection: cookie on the Blazor hub only ───────────────

    [Theory]
    [InlineData("/_blazor", WebSessionCookie.SchemeName)]
    [InlineData("/_blazor/negotiate", WebSessionCookie.SchemeName)]
    [InlineData("/api/v1/browse/x/directory", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/dav/share/file.txt", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/api/files/download", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/auth/session", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/_blazorx", JwtBearerDefaults.AuthenticationScheme)]
    public void SelectScheme_HonorsCookieOnBlazorHubOnly(string path, string expected)
        => Assert.Equal(expected, WebSessionCookie.SelectScheme(Request(path)));

    // ─────────────── Cookie attributes ───────────────

    [Fact]
    public void Append_SetsHardenedCookie()
    {
        var context = Request();
        WebSessionCookie.Append(context.Response, "jwt-value", DateTimeOffset.UtcNow.AddHours(1));

        string header = context.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.StartsWith("__host-kaimo_session=jwt-value", header);
        Assert.Contains("httponly", header);
        Assert.Contains("secure", header);
        Assert.Contains("samesite=strict", header);
        Assert.Contains("path=/", header);
        Assert.DoesNotContain("domain=", header);
    }

    // ─────────────── Origin checks ───────────────

    [Theory]
    [InlineData(null, true)]                         // non-browser client / old browser
    [InlineData(OwnOrigin, true)]
    [InlineData("https://files.example:5000", false)] // sibling service, same site
    [InlineData("https://evil.example", false)]
    [InlineData("http://files.example:8443", false)]  // scheme differs
    public void IsAllowedOrigin(string? origin, bool expected)
        => Assert.Equal(expected, WebSessionCookie.IsAllowedOrigin(Request(origin: origin).Request, []));

    [Fact]
    public void IsAllowedOrigin_ConfiguredPublicOrigin_IsAccepted()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WebSessionCookie.AllowedOriginsKey] = "https://files.public.example/, https://other.example"
            })
            .Build();

        var allowed = WebSessionCookie.ReadAllowedOrigins(config);

        Assert.True(WebSessionCookie.IsAllowedOrigin(Request(origin: "https://files.public.example").Request, allowed));
    }

    [Theory]
    [InlineData("https", OwnOrigin, "same-origin", true)]
    [InlineData("https", OwnOrigin, null, true)]
    [InlineData("https", OwnOrigin, "same-site", false)]   // sibling port/subdomain
    [InlineData("https", OwnOrigin, "cross-site", false)]
    [InlineData("https", null, "same-origin", false)]      // POST without Origin: not a browser fetch from our page
    [InlineData("https", "https://evil.example", null, false)]
    [InlineData("http", "http://files.example:8443", "same-origin", false)] // never over plain HTTP
    public void IsSameOriginFetch(string scheme, string? origin, string? fetchSite, bool expected)
        => Assert.Equal(expected,
            WebSessionCookie.IsSameOriginFetch(Request(scheme: scheme, origin: origin, fetchSite: fetchSite).Request, []));

    // ─────────────── Ticket store ───────────────

    [Fact]
    public void TicketStore_TicketIsSingleUse()
    {
        var store = new WebSessionTicketStore();
        var id = store.Issue(new WebSessionTicket("jwt", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.True(store.TryConsume(id, out var ticket));
        Assert.Equal("jwt", ticket!.Token);
        Assert.False(store.TryConsume(id, out _));
        Assert.False(store.TryConsume("unknown", out _));
    }

    // ─────────────── /auth endpoints ───────────────

    private static WebSessionController Controller(WebSessionTicketStore store, HttpContext context)
        => new(store, EmptyConfig) { ControllerContext = new ControllerContext { HttpContext = context } };

    [Fact]
    public void Establish_SameOriginFetch_SetsCookie()
    {
        var store = new WebSessionTicketStore();
        var id = store.Issue(new WebSessionTicket("jwt", DateTimeOffset.UtcNow.AddHours(1)));
        var context = Request("/auth/session", origin: OwnOrigin, fetchSite: "same-origin");

        var result = Controller(store, context).Establish(new WebSessionController.SessionTicketRequest(id));

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("__Host-kaimo_session=jwt", context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Establish_CrossSite_IsRejectedAndTicketKept()
    {
        var store = new WebSessionTicketStore();
        var id = store.Issue(new WebSessionTicket("jwt", DateTimeOffset.UtcNow.AddHours(1)));
        var context = Request("/auth/session", origin: "https://evil.example", fetchSite: "cross-site");

        var result = Controller(store, context).Establish(new WebSessionController.SessionTicketRequest(id));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Establish_ReplayedTicket_IsRejected()
    {
        var store = new WebSessionTicketStore();
        var id = store.Issue(new WebSessionTicket("jwt", DateTimeOffset.UtcNow.AddHours(1)));
        Controller(store, Request("/auth/session", origin: OwnOrigin))
            .Establish(new WebSessionController.SessionTicketRequest(id));

        var replay = Request("/auth/session", origin: OwnOrigin);
        var result = Controller(store, replay).Establish(new WebSessionController.SessionTicketRequest(id));

        Assert.IsType<BadRequestResult>(result);
        Assert.Empty(replay.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Logout_CrossSite_KeepsCookie()
    {
        var context = Request("/auth/logout", origin: "https://evil.example", fetchSite: "cross-site");

        var result = Controller(new WebSessionTicketStore(), context).Logout();

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
    }

    // ─────────────── WebDAV: ambient Basic credentials ───────────────

    [Theory]
    [InlineData(null, false)]         // native WebDAV client
    [InlineData("none", false)]       // typed URL / bookmark
    [InlineData("same-origin", false)]
    [InlineData("same-site", true)]
    [InlineData("cross-site", true)]
    public void WebDav_RejectsCrossSiteBrowserRequests(string? fetchSite, bool rejected)
        => Assert.Equal(rejected,
            WebDavEnabledMiddleware.IsCrossSiteBrowserRequest(Request("/dav/x", fetchSite: fetchSite).Request));

    // ─────────────── Session validation ───────────────

    [Fact]
    public async Task Validation_DeviceToken_IsNotAWebSession()
    {
        var userId = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(userId)).ReturnsAsync(
            new User(userId, "Alice", "alice", "pw", "nt", isEnabled: true) { SecurityStamp = "s" });
        var jwt = new JwtTokenService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
            })
            .Build(), NullLogger<JwtTokenService>.Instance);

        ClaimsPrincipal Principal(Guid? device) => jwt.ValidateToken(
            jwt.GenerateToken(userId, "alice", "Alice", ["User"], deviceId: device, securityStamp: "s"))!;

        var revoked = new Mock<IRevokedWebTokenRepository>().Object;
        Assert.Null(await WebSessionValidation.CheckAsync(Principal(null), users.Object, revoked));
        Assert.NotNull(await WebSessionValidation.CheckAsync(Principal(Guid.NewGuid()), users.Object, revoked));
    }
}
