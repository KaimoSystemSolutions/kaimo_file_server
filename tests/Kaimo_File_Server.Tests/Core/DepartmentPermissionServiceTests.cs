using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Department default permissions against the real repositories: the VALUE is inherited
/// up the department chain, while MEMBERSHIP is hierarchy-aware downwards (a member of a
/// sub-department also counts for shares of its ancestors, never the other way round).
/// Mirrors the Engineering/Backend example in <see cref="DepartmentPermissionService"/>.
/// </summary>
public sealed class DepartmentPermissionServiceTests : DatabaseTestBase
{
    private const long ReadWrite = (long)(FilePermission.ListReadData | FilePermission.CreateWriteData);

    private readonly Department _engineering;
    private readonly Department _backend;
    private readonly DepartmentPermissionService _sut;

    public DepartmentPermissionServiceTests()
    {
        _engineering = SeedDepartment("Engineering", defaultFilePermission: ReadWrite);
        _backend = SeedDepartment("Backend", parentId: _engineering.Id);
        _sut = new DepartmentPermissionService(DepartmentRepo(), ShareRepo(), GroupRepo());
    }

    private User SeedMember(string name, Department dept)
    {
        var user = SeedUser(name);
        using var db = NewContext();
        db.DepartmentUsers.Add(new DepartmentUser(dept.Id, user.Id));
        db.SaveChanges();
        return user;
    }

    private Group SeedGroup(Department dept)
    {
        var group = new Group(Guid.NewGuid(), "G-" + dept.Name, dept.Id);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    [Fact]
    public async Task OwnDefault_IsReportedAsOwn()
    {
        var info = await _sut.GetPermissionInfoAsync(_engineering.Id);

        Assert.Equal(ReadWrite, info.EffectivePermission);
        Assert.True(info.IsOwnPermission);
    }

    [Fact]
    public async Task NullDefault_InheritsFromNearestAncestor()
    {
        var info = await _sut.GetPermissionInfoAsync(_backend.Id);

        Assert.Equal(ReadWrite, info.EffectivePermission);
        Assert.False(info.IsOwnPermission);
        Assert.Equal(_engineering.Id, info.InheritedFromDepartmentId);
    }

    [Fact]
    public async Task NoDefaultInChain_OrUnknownDepartment_IsZero()
    {
        var orphan = SeedDepartment("Orphan");

        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionAsync(orphan.Id));
        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task User_DirectAndSubDepartmentMembers_GetAncestorShareDefault()
    {
        var share = SeedShare("Docs", departmentId: _engineering.Id);
        var alice = SeedMember("alice", _engineering);
        var bob = SeedMember("bob", _backend);

        Assert.Equal(ReadWrite, await _sut.GetEffectiveDefaultPermissionForUserOnShareAsync(alice.Id, share.Id));
        Assert.Equal(ReadWrite, await _sut.GetEffectiveDefaultPermissionForUserOnShareAsync(bob.Id, share.Id));
    }

    [Fact]
    public async Task User_ParentMemberOrOutsider_GetsNothingOnSubDepartmentShare()
    {
        var share = SeedShare("BackendOnly", departmentId: _backend.Id);
        var alice = SeedMember("alice", _engineering);
        var outsider = SeedUser("eve");

        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionForUserOnShareAsync(alice.Id, share.Id));
        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionForUserOnShareAsync(outsider.Id, share.Id));
        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionForUserOnShareAsync(alice.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Group_OnlySameDepartmentGetsDefault()
    {
        var share = SeedShare("Docs", departmentId: _engineering.Id);
        var engGroup = SeedGroup(_engineering);
        var backendGroup = SeedGroup(_backend);

        Assert.Equal(ReadWrite, await _sut.GetEffectiveDefaultPermissionForGroupOnShareAsync(engGroup.Id, share.Id));
        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionForGroupOnShareAsync(backendGroup.Id, share.Id));
        Assert.Equal(0, await _sut.GetEffectiveDefaultPermissionForGroupOnShareAsync(Guid.NewGuid(), share.Id));
    }
}
