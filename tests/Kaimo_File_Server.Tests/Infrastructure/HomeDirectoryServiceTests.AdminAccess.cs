using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// <see cref="ManagementPermission.AccessHomeContents"/>: an administrator reaches the homes of
/// the users in their scope on every transport, because the grant is evaluated in the
/// <see cref="AclService"/> itself. Homes outside the scope stay private.
/// </summary>
public sealed partial class HomeDirectoryServiceTests
{
    /// <summary>Lets <paramref name="admin"/> access exactly the homes of <paramref name="owners"/>.</summary>
    private void GrantHomeAccess(User admin, params User[] owners)
    {
        _mgmt.Setup(m => m.HasAnyPermissionAsync(
                It.Is<UserContext>(c => c.User.Id == admin.Id), ManagementPermission.AccessHomeContents))
            .ReturnsAsync(true);
        foreach (var owner in owners)
            _mgmt.Setup(m => m.CanManageUserAsync(
                    It.Is<UserContext>(c => c.User.Id == admin.Id), owner.Id, ManagementPermission.AccessHomeContents))
                .ReturnsAsync(true);
    }

    [Fact]
    public async Task Home_access_permission_grants_read_write_in_homes_within_scope()
    {
        var alice = AddUser("alice");
        var admin = AddUser("admin");
        admin.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, admin.Id);
        GrantHomeAccess(admin, alice);
        var home = HomeDirectoryService.HomePathOf(alice.Id);

        Assert.True(await Can(admin, "", true, FilePermission.ListReadData));            // share root / tree connect
        Assert.True(await Can(admin, home, true, FilePermission.ListReadData));
        Assert.True(await Can(admin, home, true, FilePermission.CreateWriteData));
        Assert.True(await Can(admin, $"{home}/docs/a.txt", false, FilePermission.CreateWriteData));
        Assert.True(await Can(admin, $"{home}/docs/a.txt", false, FilePermission.Delete));
        Assert.True(await Can(admin, $"{home}/.RECYCLE_BIN/a.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Home_access_permission_mirrors_the_owner_limits()
    {
        var alice = AddUser("alice");
        var admin = AddUser("admin");
        await _sut.ConfigureAsync(_root, admin.Id);
        GrantHomeAccess(admin, alice);
        var home = HomeDirectoryService.HomePathOf(alice.Id);

        Assert.False(await Can(admin, home, true, FilePermission.Delete));               // the home itself
        Assert.False(await Can(admin, "", true, FilePermission.CreateWriteData));        // nothing next to homes
        Assert.False(await Can(admin, $"{home}/a", true, FilePermission.ChangePermissions));
        Assert.False(await Can(admin, "stray.txt", false, FilePermission.ListReadData)); // not a home
    }

    [Fact]
    public async Task Home_access_permission_never_reaches_homes_outside_scope()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var admin = AddUser("admin");
        admin.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, admin.Id);
        GrantHomeAccess(admin, alice);
        var bobHome = HomeDirectoryService.HomePathOf(bob.Id);

        Assert.False(await Can(admin, bobHome, true, FilePermission.ListReadData));
        Assert.False(await Can(admin, $"{bobHome}/x.txt", false, FilePermission.ListReadData));
        // ... and an owner without the permission still sees only their own home.
        Assert.False(await Can(alice, bobHome, true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Home_access_permission_filters_the_root_listing_to_homes_in_scope()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var admin = AddUser("admin");
        admin.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, admin.Id);
        GrantHomeAccess(admin, alice);
        var aliceHome = HomeDirectoryService.HomePathOf(alice.Id);
        var bobHome = HomeDirectoryService.HomePathOf(bob.Id);

        var result = await _aclService.HasAccessBatchAsync(Ctx(admin), _shares.Single().Id,
            [(aliceHome, true), (bobHome, true), ($"{aliceHome}/a.txt", false)], FilePermission.ListReadData);

        Assert.True(result[aliceHome]);
        Assert.False(result[bobHome]);
        Assert.True(result[$"{aliceHome}/a.txt"]);
    }

    [Fact]
    public async Task Home_access_batch_evaluates_all_owners_in_one_evaluation_scope()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var admin = AddUser("admin");
        admin.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, admin.Id);
        GrantHomeAccess(admin, alice, bob);
        var aliceHome = HomeDirectoryService.HomePathOf(alice.Id);
        var bobHome = HomeDirectoryService.HomePathOf(bob.Id);

        await _aclService.HasAccessBatchAsync(Ctx(admin), _shares.Single().Id,
            [(aliceHome, true), (bobHome, true)], FilePermission.ListReadData);

        _mgmt.Verify(m => m.BeginEvaluationScope(), Times.Once);
    }

    [Fact]
    public async Task Home_access_permission_does_not_open_regular_shares()
    {
        var admin = AddUser("admin");
        var share = new ShareDefinition("projects", Path.Combine(_root, "projects"));
        _shares.Add(share);
        _mgmt.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(true);
        _mgmt.Setup(m => m.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>())).ReturnsAsync(true);
        _dept.DefaultFilePermission = null;

        Assert.False(await _aclService.HasAccessAsync(Ctx(admin), share.Id, admin.Id.ToString("D"), true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Smb_share_list_also_allows_holders_of_the_home_access_permission()
    {
        var alice = AddUser("alice");
        var dave = AddUser("dave");                         // via a department-scoped role
        dave.HomeDirectoryEnabled = false;
        var erin = AddUser("erin");                         // via a group
        erin.HomeDirectoryEnabled = false;
        var frank = AddUser("frank");                       // a role without the permission
        frank.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, alice.Id);

        var homeRole = new Role(Guid.NewGuid(), "HomeAuditor", ManagementPermission.AccessHomeContents, false);
        var otherRole = new Role(Guid.NewGuid(), "Viewer", ManagementPermission.ViewDepartment, false);
        var group = new Group(Guid.NewGuid(), "auditors");
        var roles = new Mock<IRoleRepository>();
        roles.Setup(r => r.GetAllAsync()).ReturnsAsync([homeRole, otherRole]);
        var assignments = new Mock<IScopedRoleAssignmentRepository>();
        assignments.Setup(r => r.GetByRoleAsync(homeRole.Id)).ReturnsAsync(
        [
            new ScopedRoleAssignment(dave.Id, homeRole.Id, ScopeType.Department, Guid.NewGuid()),
            new ScopedRoleAssignment(group.Id, homeRole.Id, ScopeType.Global, Guid.Empty),
        ]);
        assignments.Setup(r => r.GetByRoleAsync(otherRole.Id)).ReturnsAsync(
            [new ScopedRoleAssignment(frank.Id, otherRole.Id, ScopeType.Global, Guid.Empty)]);
        var groups = new Mock<IGroupRepository>();
        groups.Setup(r => r.GetAllAsync()).ReturnsAsync([group]);
        groups.Setup(r => r.GetMembersAsync(group.Id)).ReturnsAsync([erin]);

        var reply = await CreateShareBridge(assignments.Object, roles.Object, groups.Object)
            .ListShares(new Kaimo_File_Server.SmbBridge.Grpc.ListSharesRequest(), null!);

        var home = reply.Shares.Single(s => s.Name == HomeDirectoryService.ShareName);
        Assert.Equal(["alice", "dave", "erin"], home.AllowedUsers.Order());
    }

    private Kaimo_File_Server.SmbBridge.Services.ShareGrpcService CreateShareBridge(
        IScopedRoleAssignmentRepository? assignments = null, IRoleRepository? roles = null, IGroupRepository? groups = null)
    {
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync(() => _shares.ToList());
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _users.ToList());
        return new Kaimo_File_Server.SmbBridge.Services.ShareGrpcService(
            shares.Object, users.Object,
            assignments ?? Mock.Of<IScopedRoleAssignmentRepository>(),
            roles ?? Mock.Of<IRoleRepository>(),
            groups ?? Mock.Of<IGroupRepository>(),
            NullLogger<Kaimo_File_Server.SmbBridge.Services.ShareGrpcService>.Instance);
    }
}
