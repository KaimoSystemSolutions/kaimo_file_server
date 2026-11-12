using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Services;

/// <summary>
/// Pins the stale-while-revalidate contract of <see cref="PrincipalDirectoryCache"/>: a cached
/// snapshot is served instantly, a revalidation swaps it only when the data really changed,
/// concurrent revalidations share one load, and a failed load leaves the cache usable.
/// </summary>
public sealed class PrincipalDirectoryCacheTests
{
    private static PrincipalDirectory Directory(params string[] userNames) => new(
        userNames.Select(n => new PrincipalSummary(StableId(n), n, n.ToLowerInvariant())).ToList(),
        [],
        []);

    private static Guid StableId(string name) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    private sealed class Loader
    {
        public int Calls;
        public PrincipalDirectory Next = Directory("Alice");
        public Exception? Fail;
        public TaskCompletionSource<PrincipalDirectory>? Gate;

        public Task<PrincipalDirectory> LoadAsync()
        {
            Calls++;
            if (Fail is { } ex) return Task.FromException<PrincipalDirectory>(ex);
            return Gate?.Task ?? Task.FromResult(Next);
        }
    }

    private static PrincipalDirectoryCache Cache(Loader loader, TimeSpan? interval = null) =>
        new(loader.LoadAsync, interval ?? TimeSpan.Zero);

    [Fact]
    public async Task GetAsync_Cold_LoadsOnce()
    {
        var loader = new Loader();
        var cache = Cache(loader);

        var directory = await cache.GetAsync();

        Assert.Equal(1, loader.Calls);
        Assert.Same(directory, cache.Current);
        Assert.Equal("Alice", Assert.Single(directory.Users).Name);
    }

    [Fact]
    public async Task GetAsync_Warm_ServesCachedSnapshotWithoutLoading()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var first = await cache.GetAsync();
        loader.Next = Directory("Alice", "Bob");

        var second = await cache.GetAsync();

        Assert.Equal(1, loader.Calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task RevalidateAsync_DataChanged_SwapsSnapshot()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var first = await cache.GetAsync();
        loader.Next = Directory("Alice", "Bob");

        await cache.RevalidateAsync();

        Assert.NotSame(first, cache.Current);
        Assert.Equal(2, cache.Current!.Users.Count);
    }

    [Fact]
    public async Task RevalidateAsync_DataUnchanged_KeepsSameSnapshotInstance()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var first = await cache.GetAsync();
        loader.Next = Directory("Alice"); // equal content, new instance

        await cache.RevalidateAsync();

        Assert.Equal(2, loader.Calls);
        Assert.Same(first, cache.Current);
    }

    [Fact]
    public async Task RevalidateAsync_RenamedPrincipal_CountsAsChange()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var first = await cache.GetAsync();
        var id = first.Users[0].Id;
        loader.Next = new PrincipalDirectory([new PrincipalSummary(id, "Alice Renamed", "alice")], [], []);

        await cache.RevalidateAsync();

        Assert.Equal("Alice Renamed", cache.Current!.Users[0].Name);
    }

    [Fact]
    public async Task RevalidateAsync_WithinMinimumInterval_SkipsLoad()
    {
        var loader = new Loader();
        var cache = Cache(loader, TimeSpan.FromMinutes(5));
        await cache.GetAsync();

        await cache.RevalidateAsync();

        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_Cold_Loads()
    {
        var loader = new Loader();
        var cache = Cache(loader, TimeSpan.FromMinutes(5));

        await cache.RevalidateAsync();

        Assert.Equal(1, loader.Calls);
        Assert.NotNull(cache.Current);
    }

    [Fact]
    public async Task RevalidateAsync_Concurrent_SharesOneLoad()
    {
        var loader = new Loader { Gate = new TaskCompletionSource<PrincipalDirectory>() };
        var cache = Cache(loader);

        var a = cache.RevalidateAsync();
        var b = cache.RevalidateAsync();
        var c = cache.GetAsync();
        loader.Gate.SetResult(Directory("Alice"));
        await Task.WhenAll(a, b, c);

        Assert.Equal(1, loader.Calls);
        Assert.Same(cache.Current, await c);
    }

    [Fact]
    public async Task RevalidateAsync_LoadFails_KeepsSnapshotAndRetriesNextTime()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var first = await cache.GetAsync();

        loader.Fail = new InvalidOperationException("db down");
        await Assert.ThrowsAsync<InvalidOperationException>(cache.RevalidateAsync);
        Assert.Same(first, cache.Current);

        loader.Fail = null;
        loader.Next = Directory("Alice", "Bob");
        await cache.RevalidateAsync();
        Assert.Equal(2, cache.Current!.Users.Count);
    }

    [Fact]
    public async Task LoadAsync_ProjectsAndSortsUsersGroupsAndRoles()
    {
        var users = new Mock<IUserRepository>();
        var groups = new Mock<IGroupRepository>();
        var roles = new Mock<IRoleRepository>();
        var bobId = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        users.Setup(u => u.GetSummariesAsync()).ReturnsAsync(new List<PrincipalSummary>
        {
            new(bobId, "bob", "bob"),
            new(aliceId, "Alice", "alice"),
        });
        var group = new Group(Guid.NewGuid(), "Sales");
        groups.Setup(g => g.GetAllAsync()).ReturnsAsync(new List<Group> { group });
        var role = new Role(Guid.NewGuid(), "Auditor");
        roles.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Role> { role });

        var directory = await PrincipalDirectoryCache.LoadAsync(users.Object, groups.Object, roles.Object);

        Assert.Equal(new[] { aliceId, bobId }, directory.Users.Select(u => u.Id));
        Assert.Equal(new PrincipalSummary(group.Id, "Sales"), Assert.Single(directory.Groups));
        Assert.Equal(new PrincipalSummary(role.Id, "Auditor"), Assert.Single(directory.Roles));
        users.Verify(u => u.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_SortsUmlautsAlphabetically_NotAfterZ()
    {
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetSummariesAsync()).ReturnsAsync(new List<PrincipalSummary>
        {
            new(Guid.NewGuid(), "Zoe"),
            new(Guid.NewGuid(), "Ärger"),
            new(Guid.NewGuid(), "anton"),
        });
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetAllAsync()).ReturnsAsync(new List<Group>());
        var roles = new Mock<IRoleRepository>();
        roles.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Role>());

        var directory = await PrincipalDirectoryCache.LoadAsync(users.Object, groups.Object, roles.Object);

        Assert.Equal(new[] { "anton", "Ärger", "Zoe" }, directory.Users.Select(u => u.Name));
    }

    [Fact]
    public async Task RevalidateSinceAsync_ReturnsNewerSnapshot_OrNullWhenUnchanged()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var seen = await cache.GetAsync();

        Assert.Null(await cache.RevalidateSinceAsync(seen, NullLogger.Instance));

        loader.Next = Directory("Alice", "Bob");
        var newer = await cache.RevalidateSinceAsync(seen, NullLogger.Instance);
        Assert.Same(cache.Current, newer);
    }

    [Fact]
    public async Task RevalidateSinceAsync_LoadFails_ReturnsNullInsteadOfThrowing()
    {
        var loader = new Loader();
        var cache = Cache(loader);
        var seen = await cache.GetAsync();
        loader.Fail = new InvalidOperationException("db down");

        Assert.Null(await cache.RevalidateSinceAsync(seen, NullLogger.Instance));
    }

    [Fact]
    public void PickerBindings_ProjectSummaries_WithEmailOrUsernameHint()
    {
        var withMail = new PrincipalSummary(Guid.NewGuid(), "Alice", "alice", "alice@example.com");
        var withoutMail = new PrincipalSummary(Guid.NewGuid(), "Bob", "bob", " ");
        var group = new PrincipalSummary(Guid.NewGuid(), "Sales");
        var role = new PrincipalSummary(Guid.NewGuid(), "Auditor");

        var users = new[] { withMail, withoutMail }.ToUserItems();

        Assert.Equal(new[] { "alice@example.com", "bob" }, users.Select(i => i.Hint));
        Assert.All(users, i => Assert.Equal(PrincipalKind.User, i.Kind));
        Assert.Equal(new PrincipalPickerItem(group.Id, "Sales", PrincipalKind.Group), Assert.Single(new[] { group }.ToGroupItems()));
        Assert.Equal(new PrincipalPickerItem(role.Id, "Auditor", PrincipalKind.Role), Assert.Single(new[] { role }.ToRoleItems()));
    }
}
