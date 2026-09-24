using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Kaimo_File_Server.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// HTTP Basic authentication for the WebDAV endpoint. Delegates credential
/// verification to <see cref="ILoginService"/>, so the same brute-force throttle
/// and username-enumeration resistance as the web login apply unchanged.
///
/// Two protocol details a WebDAV mount depends on:
/// <list type="bullet">
/// <item>A single Explorer directory open issues dozens of Basic requests, each
/// otherwise re-running a ~100 ms BCrypt verification. Successful verifications
/// are cached for 60 s (keyed by the username plus an HMAC of the password, so
/// the plaintext never lives in the cache). Failures are never cached, so the
/// login throttle still sees every bad attempt.</item>
/// <item>Basic is refused over plain HTTP with <c>426 Upgrade Required</c> unless
/// <see cref="WebDavOptions.RequireHttpsKey"/> is turned off for a deployment that
/// terminates TLS in front. The <c>401</c> challenge offers <c>Basic</c> only and
/// never <c>Negotiate</c>, which the Windows redirector cannot satisfy.</item>
/// </list>
/// </summary>
public sealed class WebDavBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "webdav-basic";
    public const string Realm = "Kaimo File Server";

    private const int PositiveCacheTtlSeconds = 60;
    private const string TlsRequiredItemKey = "webdav.basic.tlsRequired";

    // Per-process random key so a cached password fingerprint is not portable
    // across restarts and cannot be precomputed from the plaintext offline.
    private static readonly byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);

    private readonly ILoginService _loginService;
    private readonly IMemoryCache _cache;
    private readonly WebDavOptions _options;

    public WebDavBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ILoginService loginService,
        IMemoryCache cache,
        WebDavOptions webDavOptions)
        : base(options, logger, encoder)
    {
        _loginService = loginService;
        _cache = cache;
        _options = webDavOptions;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var settings = await _options.GetAsync();

        if (settings.RequireHttps && !Request.IsHttps)
        {
            Context.Items[TlsRequiredItemKey] = true;
            return AuthenticateResult.Fail("WebDAV Basic authentication requires HTTPS.");
        }

        string header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        if (!TryDecodeCredentials(header["Basic ".Length..].Trim(), out var username, out var password))
            return AuthenticateResult.Fail("Malformed Basic credentials.");

        var cacheKey = FingerprintCacheKey(username, password);
        if (_cache.TryGetValue<Guid>(cacheKey, out var cachedUserId))
            return Success(cachedUserId, username);

        var result = await _loginService.AuthenticateAsync(
            username, password, Context.Connection.RemoteIpAddress?.ToString());
        if (result.Outcome != LoginOutcome.Success || result.UserContext is null)
            return AuthenticateResult.Fail("Invalid credentials.");

        var userId = result.UserContext.User.Id;
        _cache.Set(cacheKey, userId, TimeSpan.FromSeconds(PositiveCacheTtlSeconds));
        return Success(userId, username);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(TlsRequiredItemKey, out var flag) && flag is true)
        {
            Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            Response.Headers.Upgrade = "TLS/1.2, HTTP/1.1";
            Response.Headers.Connection = "Upgrade";
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // Basic only — never Negotiate: the Windows redirector aborts the mount
        // when it is offered a scheme it cannot satisfy.
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
        return Task.CompletedTask;
    }

    private AuthenticateResult Success(Guid userId, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static bool TryDecodeCredentials(string base64, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        Span<byte> buffer = stackalloc byte[512];
        string decoded;
        if (Convert.TryFromBase64String(base64, buffer, out var written))
        {
            decoded = Encoding.UTF8.GetString(buffer[..written]);
        }
        else
        {
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
            catch (FormatException) { return false; }
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
            return false;

        username = decoded[..separator];
        password = decoded[(separator + 1)..];
        return username.Length > 0;
    }

    private static string FingerprintCacheKey(string username, string password)
    {
        var input = Encoding.UTF8.GetBytes($"{username}\0{password}");
        var mac = HMACSHA256.HashData(FingerprintKey, input);
        return $"webdav-basic:{Convert.ToHexStringLower(mac)}";
    }
}
