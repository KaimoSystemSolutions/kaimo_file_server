using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Kaimo_File_Server.Web.Services;

public class JwtTokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly int _expirationHours;

    public JwtTokenService(IConfiguration config)
    {
        _secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _issuer = config["Jwt:Issuer"] ?? "KaimoFileServer";
        _expirationHours = config.GetValue<int>("Jwt:ExpirationHours", 24);

        Console.WriteLine($"[JWT] Initialisiert: Issuer={_issuer}, Secret-Länge={_secret.Length}, Expiration={_expirationHours}h");
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
        Console.WriteLine($"[JWT] Token generiert: expires={token.ValidTo:u}, length={tokenString.Length}, roles={string.Join(",", roles)}");
        return tokenString;
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
            var handler = new JwtSecurityTokenHandler();

            // Erst mal den Token lesen ohne Validierung, um zu sehen was drin ist
            if (handler.CanReadToken(token))
            {
                var jwt = handler.ReadJwtToken(token);
                Console.WriteLine($"[JWT] Token lesen: Issuer={jwt.Issuer}, Audience={jwt.Audiences.FirstOrDefault()}, Expires={jwt.ValidTo:u}, Now={DateTime.UtcNow:u}");
            }
            else
            {
                Console.WriteLine($"[JWT] Token kann nicht gelesen werden! Erste 50 Zeichen: {token[..Math.Min(50, token.Length)]}");
                return null;
            }

            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _issuer,
                ValidateLifetime = true,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);

            Console.WriteLine($"[JWT] Validierung OK: {principal.Identity?.Name}");
            return principal;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JWT] Validierung FEHLGESCHLAGEN: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}