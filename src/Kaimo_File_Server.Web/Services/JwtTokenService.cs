using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Kaimo_File_Server.Web.Services;

public class JwtTokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly int _expirationHours;
    private readonly ILogger<JwtTokenService> _logger;

    private const int MinSecretLength = 32; // 256-bit minimum for HMAC-SHA256

    /// <summary>
    /// Claim carrying the client-API device registration id. Present only on tokens
    /// minted for a device (the REST client API); absent on the Blazor web login
    /// token. The API's per-request auth uses it to reject a revoked device at once.
    /// </summary>
    public const string DeviceIdClaim = "device_id";

    /// <summary>
    /// Secrets that have shipped in source control / documentation and are
    /// therefore public knowledge. Since forging a valid token requires nothing
    /// more than the signing secret, a well-known value is equivalent to having
    /// no authentication at all — we refuse to start with one. Compared
    /// case-insensitively and trimmed so trivial variations are caught too.
    /// </summary>
    private static readonly HashSet<string> ForbiddenSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "KaimoFileServer_SuperSecret_Key_ChangeThis_Min32Chars!!",
        "changethis",
        "change_me",
        "changeme",
        "secret",
        "supersecret",
    };

    public JwtTokenService(IConfiguration config, ILogger<JwtTokenService> logger)
    {
        _logger = logger;
        _secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _issuer = config["Jwt:Issuer"] ?? "KaimoFileServer";
        _expirationHours = config.GetValue<int>("Jwt:ExpirationHours", 24);

        if (_secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException(
                $"Jwt:Secret muss mindestens {MinSecretLength} Zeichen lang sein (aktuell: {_secret.Length}). " +
                "Ein kürzerer Secret ist unsicher für HMAC-SHA256.");
        }

        if (ForbiddenSecrets.Contains(_secret.Trim()))
        {
            throw new InvalidOperationException(
                "Jwt:Secret ist ein bekannter Default-/Beispielwert und gilt als kompromittiert. " +
                "Bitte ein zufälliges, geheimes Secret (>= 32 Zeichen) über Umgebungsvariable " +
                "(Jwt__Secret) oder User-Secrets setzen — niemals im Repository ablegen.");
        }

        _logger.LogInformation("JWT initialisiert: Issuer={Issuer}, Expiration={Hours}h", _issuer, _expirationHours);
    }

    /// <param name="deviceId">
    /// When set, stamps a <see cref="DeviceIdClaim"/> so the client API can bind the
    /// token to a device and reject it the moment that device is revoked. Left null
    /// for the Blazor web login, whose tokens are not device-scoped.
    /// </param>
    public string GenerateToken(
        Guid userId, string username, string displayName, IEnumerable<string> roles,
        Guid? deviceId = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new("display_name", displayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (deviceId is { } id)
            claims.Add(new Claim(DeviceIdClaim, id.ToString()));

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expirationHours),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        _logger.LogDebug("Token generated for {Username}, valid until {Expires}",
            username, token.ValidTo);
        return tokenString;
    }

    /// <summary>
    /// Builds the single, canonical set of validation parameters used by BOTH
    /// the Blazor <see cref="ValidateToken"/> path and the REST API's JWT bearer
    /// handler, so the two can never drift apart. The algorithm is pinned to
    /// HMAC-SHA256 so a token cannot be presented with a forged "alg" header
    /// (e.g. "none" or an asymmetric algorithm) to sidestep HMAC verification.
    /// </summary>
    public static TokenValidationParameters CreateValidationParameters(string secret, string issuer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = issuer,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    }

    /// <summary>Validation parameters for this instance's configured secret/issuer.</summary>
    public TokenValidationParameters ValidationParameters => CreateValidationParameters(_secret, _issuer);

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();

            if (!handler.CanReadToken(token))
            {
                _logger.LogWarning("Token cannot be read");
                return null;
            }

            var principal = handler.ValidateToken(token, ValidationParameters, out _);

            return principal;
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogInformation("Token expired");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Token validation failed: {Error}", ex.Message);
            return null;
        }
    }
}