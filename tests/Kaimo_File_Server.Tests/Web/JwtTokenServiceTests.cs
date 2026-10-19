using System.Security.Claims;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards the JWT signing/validation contract. Two invariants matter most:
///   1. The service refuses to start with a known/weak signing secret — a
///      public secret means anyone can forge admin tokens.
///   2. Validation rejects tampered, foreign-signed, and expired tokens.
/// </summary>
public class JwtTokenServiceTests
{
    private const string StrongSecret = "ThisIsALongEnoughTestSecretValue_1234567890!";

    private static JwtTokenService Create(string secret, int expirationHours = 24)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = secret,
                ["Jwt:Issuer"] = "KaimoFileServer",
                ["Jwt:ExpirationHours"] = expirationHours.ToString(),
            })
            .Build();

        return new JwtTokenService(config, NullLogger<JwtTokenService>.Instance);
    }

    // ─────────────── Secret hardening ───────────────

    [Fact]
    public void Ctor_KnownCommittedDefaultSecret_Throws()
    {
        // The exact value that used to live in appsettings.json. It is public
        // knowledge and must be rejected even though it is long enough.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Create("KaimoFileServer_SuperSecret_Key_ChangeThis_Min32Chars!!"));
        Assert.Contains("Default", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("changeme")]
    [InlineData("ChangeThis")]
    [InlineData("secret")]
    [InlineData("change_me")]
    public void Ctor_WellKnownWeakSecret_Throws(string secret)
        => Assert.Throws<InvalidOperationException>(() => Create(secret));

    [Fact]
    public void Ctor_TooShortSecret_Throws()
        => Assert.Throws<InvalidOperationException>(() => Create("short"));

    [Fact]
    public void Ctor_EmptySecret_Throws()
        => Assert.Throws<InvalidOperationException>(() => Create(""));

    private const string DevComposeSecret = "DevOnly-LocalDevelopment-DoNotUseInProduction-JwtSecret";

    [Fact]
    public void ValidateSecret_DevelopmentSecretOutsideDevelopment_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => JwtTokenService.ValidateSecret(DevComposeSecret, allowDevelopmentSecret: false));

    [Fact]
    public void ValidateSecret_DevelopmentSecretInDevelopment_Accepted()
        => Assert.Equal(DevComposeSecret,
            JwtTokenService.ValidateSecret(DevComposeSecret, allowDevelopmentSecret: true));

    [Fact]
    public void ValidateSecret_WeakSecret_ThrowsEvenInDevelopment()
        => Assert.Throws<InvalidOperationException>(
            () => JwtTokenService.ValidateSecret("KaimoFileServer_SuperSecret_Key_ChangeThis_Min32Chars!!",
                allowDevelopmentSecret: true));

    [Fact]
    public void Ctor_StrongSecret_Succeeds()
    {
        var sut = Create(StrongSecret);
        Assert.NotNull(sut);
    }

    // ─────────────── Round trip ───────────────

    [Fact]
    public void GenerateThenValidate_RoundTripsClaims()
    {
        var sut = Create(StrongSecret);
        var userId = Guid.NewGuid();

        var token = sut.GenerateToken(userId, "alice", "Alice Admin", new[] { "Administrator", "User" });
        var principal = sut.ValidateToken(token);

        Assert.NotNull(principal);
        Assert.Equal("alice", principal!.Identity?.Name);
        Assert.Equal(userId.ToString(),
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(principal.IsInRole("Administrator"));
        Assert.True(principal.IsInRole("User"));
        Assert.False(principal.IsInRole("Nope"));
    }

    // ─────────────── Rejection paths ───────────────

    [Fact]
    public void ValidateToken_Garbage_ReturnsNull()
    {
        var sut = Create(StrongSecret);
        Assert.Null(sut.ValidateToken("not.a.jwt"));
        Assert.Null(sut.ValidateToken(""));
    }

    [Fact]
    public void ValidateToken_TamperedPayload_ReturnsNull()
    {
        var sut = Create(StrongSecret);
        var token = sut.GenerateToken(Guid.NewGuid(), "bob", "Bob", new[] { "User" });

        // Flip a character in the payload segment → signature no longer matches.
        var parts = token.Split('.');
        var payload = parts[1].ToCharArray();
        payload[0] = payload[0] == 'A' ? 'B' : 'A';
        parts[1] = new string(payload);
        var tampered = string.Join('.', parts);

        Assert.Null(sut.ValidateToken(tampered));
    }

    [Fact]
    public void ValidateToken_SignedWithDifferentSecret_ReturnsNull()
    {
        var issuer = Create(StrongSecret);
        var token = issuer.GenerateToken(Guid.NewGuid(), "eve", "Eve", new[] { "Administrator" });

        // A service with a DIFFERENT secret must not accept the foreign token.
        var verifier = Create("AnEntirelyDifferentSecretValue_0987654321!");
        Assert.Null(verifier.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsNull()
    {
        // Negative expiration → token expired well beyond the 2-minute clock skew.
        var sut = Create(StrongSecret, expirationHours: -1);
        var token = sut.GenerateToken(Guid.NewGuid(), "old", "Old", new[] { "User" });

        Assert.Null(sut.ValidateToken(token));
    }
}
