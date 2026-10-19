using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The read-only demo guard must block EVERY database write, not only the ones that
/// go through SaveChanges. Bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> statements
/// bypass the change tracker, so the guard has to intercept them at command level too.
/// Data is seeded without the guard, then production repositories run with it.
/// </summary>
public sealed class ReadOnlyDemoInterceptorTests : DatabaseTestBase
{
    private TestDbContextFactory Demo() => FactoryWith(new ReadOnlyDemoSaveInterceptor());

    [Fact]
    public async Task TrackedSave_IsBlocked()
    {
        var user = SeedUser("alice");

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => new UserRepository(Demo()).UpdateProfileAsync(user.Id, "x", "x@example.test", true, true));
    }

    [Fact]
    public async Task ExecuteUpdate_Rename_IsBlocked()
    {
        var user = SeedUser("alice", "Alice");

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => new UserRepository(Demo()).UpdateNameAsync(user.Id, "Mallory"));

        await using var db = NewContext();
        Assert.Equal("Alice", (await db.Users.SingleAsync(u => u.Id == user.Id)).Name);
    }

    [Fact]
    public async Task ExecuteUpdate_PasswordChange_IsBlocked()
    {
        var user = SeedUser("alice");

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => new UserRepository(Demo()).UpdatePasswordAsync(user.Id, "new-hash", "new-nt"));

        await using var db = NewContext();
        Assert.Equal("pw-hash", (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash);
    }

    [Fact]
    public async Task UserDelete_LeavesUserAndMembershipsIntact()
    {
        var user = SeedUser("alice");
        var group = new Group(Guid.NewGuid(), "Staff");
        await using (var seed = NewContext())
        {
            seed.Groups.Add(group);
            seed.UserGroups.Add(new UserGroup(user.Id, group.Id));
            await seed.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => new UserRepository(Demo()).DeleteAsync(user.Id));

        await using var db = NewContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == user.Id));
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.UserId == user.Id && ug.GroupId == group.Id));
    }

    [Fact]
    public async Task ExecuteDelete_RemoveGroupMember_IsBlocked()
    {
        var user = SeedUser("alice");
        var group = new Group(Guid.NewGuid(), "Staff");
        await using (var seed = NewContext())
        {
            seed.Groups.Add(group);
            seed.UserGroups.Add(new UserGroup(user.Id, group.Id));
            await seed.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => new GroupRepository(Demo()).RemoveMemberAsync(group.Id, user.Id));

        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.UserId == user.Id));
    }

    [Fact]
    public async Task Reads_And_NoOpSave_StillWork()
    {
        var user = SeedUser("alice");
        var repo = new UserRepository(Demo());

        Assert.NotNull(await repo.GetByIdAsync(user.Id));
        Assert.Single(await repo.GetAllAsync());

        await using var db = Demo().CreateDbContext();
        Assert.Equal(0, await db.SaveChangesAsync());
        // Raw SELECT through the non-query path (e.g. advisory locks) stays allowed.
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
    }
}
