using Kaimo_File_Server.Core.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Kaimo_File_Server.Infrastructure.Repositories.Identity;

namespace Kaimo_File_Server.Infrastructure.Tests.Repositories.Identity;

/// <summary>
/// <see cref="SecondFactorRepository"/> against the real schema: confirm, replay-safe step
/// updates, single-use recovery codes, delete, and the cascade when the user is deleted.
/// </summary>
public class SecondFactorRepositoryTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private SecondFactorRepository Repo() => new(DbFactory);

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), "Erika", "erika" + Guid.NewGuid().ToString("N")[..6], "hash", "nthash");
        await using var db = NewContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Confirm_StoresFactorAndRecoveryCodes()
    {
        var userId = await SeedUserAsync();

        Assert.True(await Repo().ConfirmAsync(userId, "protected", 100, ["a", "b", "c"], Now));

        var factor = await Repo().GetAsync(userId);
        Assert.NotNull(factor);
        Assert.Equal("protected", factor.SecretProtected);
        Assert.Equal(Now, factor.ConfirmedAtUtc);
        Assert.Equal(100, factor.LastUsedStep);
        Assert.Equal(3, await Repo().CountUnusedRecoveryCodesAsync(userId));
        Assert.Contains(userId, await Repo().GetConfirmedUserIdsAsync());
    }

    [Fact]
    public async Task Confirm_WhenAlreadyConfirmed_ChangesNothing()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "first", 100, ["a"], Now);

        Assert.False(await Repo().ConfirmAsync(userId, "second", 200, ["x", "y"], Now));

        var factor = await Repo().GetAsync(userId);
        Assert.Equal("first", factor!.SecretProtected);
        Assert.Equal(1, await Repo().CountUnusedRecoveryCodesAsync(userId));
    }

    [Fact]
    public async Task TryAdvanceStep_AcceptsOnlyLaterSteps()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 100, [], Now);

        Assert.False(await Repo().TryAdvanceStepAsync(userId, 100));
        Assert.False(await Repo().TryAdvanceStepAsync(userId, 99));
        Assert.True(await Repo().TryAdvanceStepAsync(userId, 101));
        Assert.False(await Repo().TryAdvanceStepAsync(userId, 101));
        Assert.Equal(101, (await Repo().GetAsync(userId))!.LastUsedStep);
    }

    [Fact]
    public async Task TryAdvanceStep_WithoutFactor_ReturnsFalse()
    {
        Assert.False(await Repo().TryAdvanceStepAsync(await SeedUserAsync(), 5));
    }

    [Fact]
    public async Task RecoveryCode_IsSingleUseAndBoundToItsUser()
    {
        var userId = await SeedUserAsync();
        var otherId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["code1", "code2"], Now);

        Assert.False(await Repo().TryUseRecoveryCodeAsync(otherId, "code1", Now));
        Assert.True(await Repo().TryUseRecoveryCodeAsync(userId, "code1", Now));
        Assert.False(await Repo().TryUseRecoveryCodeAsync(userId, "code1", Now));
        Assert.False(await Repo().TryUseRecoveryCodeAsync(userId, "unknown", Now));
        Assert.Equal(1, await Repo().CountUnusedRecoveryCodesAsync(userId));
    }

    [Fact]
    public async Task ReplaceRecoveryCodes_DropsOldOnes()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["old1", "old2"], Now);

        await Repo().ReplaceRecoveryCodesAsync(userId, ["new1", "new2", "new3"]);

        Assert.Equal(3, await Repo().CountUnusedRecoveryCodesAsync(userId));
        Assert.False(await Repo().TryUseRecoveryCodeAsync(userId, "old1", Now));
        Assert.True(await Repo().TryUseRecoveryCodeAsync(userId, "new2", Now));
    }

    [Fact]
    public async Task Delete_RemovesFactorAndCodes()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["a", "b"], Now);

        Assert.True(await Repo().DeleteAsync(userId));

        Assert.Null(await Repo().GetAsync(userId));
        Assert.Equal(0, await Repo().CountUnusedRecoveryCodesAsync(userId));
        Assert.False(await Repo().DeleteAsync(userId));
    }

    private async Task<string> StampOfAsync(Guid userId)
    {
        await using var db = NewContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
    }

    [Fact]
    public async Task Delete_KeepsTheSecurityStampByDefault()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["a"], Now);
        var before = await StampOfAsync(userId);

        await Repo().DeleteAsync(userId);

        Assert.Equal(before, await StampOfAsync(userId));
    }

    [Fact]
    public async Task Delete_WithRotateSecurityStamp_EndsExistingSessions()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["a"], Now);
        var before = await StampOfAsync(userId);

        Assert.True(await Repo().DeleteAsync(userId, rotateSecurityStamp: true));

        Assert.NotEqual(before, await StampOfAsync(userId));
        Assert.Null(await Repo().GetAsync(userId));
    }

    [Fact]
    public async Task Delete_WithRotateSecurityStamp_WithoutFactor_KeepsTheStamp()
    {
        var userId = await SeedUserAsync();
        var before = await StampOfAsync(userId);

        Assert.False(await Repo().DeleteAsync(userId, rotateSecurityStamp: true));

        Assert.Equal(before, await StampOfAsync(userId));
    }

    [Fact]
    public async Task DeletingTheUser_CascadesToFactorAndCodes()
    {
        var userId = await SeedUserAsync();
        await Repo().ConfirmAsync(userId, "protected", 1, ["a"], Now);

        await using (var db = NewContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        }

        await using var check = NewContext();
        Assert.False(await check.UserSecondFactors.AnyAsync(f => f.UserId == userId));
        Assert.False(await check.UserRecoveryCodes.AnyAsync(c => c.UserId == userId));
    }
}
