using System.Security.Claims;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// The second-factor part of the web session check: once 2FA applies to an account (it has a
/// factor, or the policy requires one), only tokens confirmed with a second factor stay valid.
/// Client-API tokens are not web sessions and are never affected (the decision is web only).
/// </summary>
public class WebSessionSecondFactorTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly JwtTokenService _jwt = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "unit-test-jwt-secret-at-least-32-characters-long!!",
        })
        .Build(), NullLogger<JwtTokenService>.Instance);

    private readonly ConfigRepository _config;

    public WebSessionSecondFactorTests()
        => _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));

    private SecondFactorPolicyService Policy() => new(
        _config, new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(), RoleRepo(),
        new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions()));

    private ClaimsPrincipal Session(Kaimo_File_Server.Core.Domain.Identity.User user, bool secondFactor)
        => _jwt.ValidateToken(_jwt.GenerateToken(user.Id, user.Username, user.Name, [],
            securityStamp: user.SecurityStamp, authMethods: JwtTokenService.AuthMethods(secondFactor)))!;

    private Task<string?> CheckAsync(ClaimsPrincipal principal)
        => WebSessionValidation.CheckAsync(principal, UserRepo(), new Mock<IRevokedWebTokenRepository>().Object,
            TestGates.AllGloballyOn(UserRepo()), Policy());

    [Fact]
    public void AuthMethodsClaim_RoundTripsThroughTheToken()
    {
        var user = SeedUser("erika");
        Assert.True(JwtTokenService.HasSecondFactor(Session(user, secondFactor: true)));
        Assert.False(JwtTokenService.HasSecondFactor(Session(user, secondFactor: false)));
    }

    [Fact]
    public async Task PasswordOnlySession_IsValid_WhileNoSecondFactorApplies()
    {
        var user = SeedUser("erika");
        Assert.Null(await CheckAsync(Session(user, secondFactor: false)));
    }

    [Fact]
    public async Task PasswordOnlySession_Ends_OnceTheAccountHasASecondFactor()
    {
        var user = SeedUser("erika");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(user.Id, "secret", 1, [], Now);

        Assert.NotNull(await CheckAsync(Session(user, secondFactor: false)));
        Assert.Null(await CheckAsync(Session(user, secondFactor: true)));
    }

    [Fact]
    public async Task PasswordOnlySession_Ends_WhenThePolicyStartsRequiringASecondFactor()
    {
        var user = SeedUser("erika");
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });

        Assert.NotNull(await CheckAsync(Session(user, secondFactor: false)));
    }

    [Fact]
    public async Task WithoutPolicyService_TheCheckIsUnchanged()
    {
        var user = SeedUser("erika");
        await new SecondFactorRepository(DbFactory).ConfirmAsync(user.Id, "secret", 1, [], Now);

        Assert.Null(await WebSessionValidation.CheckAsync(Session(user, false), UserRepo(),
            new Mock<IRevokedWebTokenRepository>().Object, TestGates.AllGloballyOn(UserRepo())));
    }
}
