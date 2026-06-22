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

    public string GenerateToken(Guid userId, string username, string displayName, IEnumerable<string> roles)
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

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
            var handler = new JwtSecurityTokenHandler();

            if (!handler.CanReadToken(token))
            {
                _logger.LogWarning("Token cannot be read");
                return null;
            }

            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _issuer,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                // Pin the algorithm so a token cannot be presented with a
                // different/forged "alg" header (e.g. "none" or an asymmetric
                // algorithm) to sidestep HMAC verification.
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);

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