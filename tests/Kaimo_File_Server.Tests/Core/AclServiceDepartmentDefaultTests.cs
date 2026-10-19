using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Reproduces the department-default ("virtual allow") path of AclService,
/// which the existing sync-only tests do not cover.
/// </summary>
public class AclServiceDepartmentDefaultTests
{
    private const FilePermission WriteGroup =
        FilePermission.CreateWriteData | FilePermission.CreateAppendData |
        FilePermission.WriteAttributes | FilePermission.WriteExtAttributes;

    private static AclService BuildSut(
        Guid shareId, Guid deptId, long? deptDefault,
        HashSet<Department> userDepartments,
        out UserContext userContext,
        HashSet<Guid>? descendantsOfShareDept = null,
        List<Department>? departmentsById = null)
    {
        var user = new User(Guid.NewGuid(), "Test User", "testuser", "hash", "nthash");
        userContext = new UserContext(user, [], [], [], userDepartments);

        var share = new ShareDefinition("test", "/data/test", deptId);

        var byId = (departmentsById ?? [])
            .ToDictionary(d => d.Id, d => d);
        if (!byId.ContainsKey(deptId))
            byId[deptId] = new Department("Dept") { Id = deptId, DefaultFilePermission = deptDefault };

        var shareRepo = new Mock<IShareRepository>();
        shareRepo.Setup(r => r.GetByIdAsync(shareId)).ReturnsAsync(share);

        var deptRepo = new Mock<IDepartmentRepository>();
        deptRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => byId.TryGetValue(id, out var d) ? d : null);
        deptRepo.Setup(r => r.GetAncestorChainAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<Department>());
        deptRepo.Setup(r => r.GetDescendantIdsAsync(deptId))
            .ReturnsAsync(descendantsOfShareDept ?? new HashSet<Guid>());

        var aclRepo = new Mock<IAclRepository>();
        aclRepo.Setup(r => r.GetAclsForPathsAsync(shareId, It.IsAny<List<string>>()))
            .ReturnsAsync(new List<(string, bool, List<AccessEntry>)>());

        var services = new ServiceCollection();
        services.AddSingleton(shareRepo.Object);
        services.AddSingleton(deptRepo.Object);
        services.AddSingleton(aclRepo.Object);

        return new AclService(services.BuildServiceProvider());
    }

    [Fact]
    public async Task DeptDefault_ReadWrite_GrantsBothReadAndWrite()
    {
        var shareId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var dept = new Department("Dept") { Id = deptId };

        var sut = BuildSut(
            shareId, deptId,
            (long)(FilePermission.ReadAll | WriteGroup),
            [dept],
            out var ctx);

        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.ListReadData),
            "Read should be granted by the department default.");
        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.CreateWriteData),
            "Write should be granted by the department default.");
    }

    [Fact]
    public async Task DeptDefault_ReadOnly_GrantsReadButNotWrite()
    {
        var shareId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        var dept = new Department("Dept") { Id = deptId };

        var sut = BuildSut(
            shareId, deptId,
            (long)FilePermission.ReadAll,
            [dept],
            out var ctx);

        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.ListReadData));
        Assert.False(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.CreateWriteData));
    }

    [Fact]
    public async Task DeptDefault_NotAMember_GrantsNothing()
    {
        var shareId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        // User belongs to NO departments → default must not apply.
        var sut = BuildSut(
            shareId, deptId,
            (long)(FilePermission.ReadAll | WriteGroup),
            [],
            out var ctx);

        Assert.False(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.ListReadData));
        Assert.False(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.CreateWriteData));
    }

    [Fact]
    public async Task DeptDefault_SubDepartmentMember_GetsParentShareDefault()
    {
        // Share belongs to the PARENT department (Read+Write).
        // User is a member of the CHILD department only.
        // Hierarchy-aware membership → user must get the parent's Read+Write.
        var shareId = Guid.NewGuid();
        var parentDeptId = Guid.NewGuid();
        var childDeptId = Guid.NewGuid();

        var childDept = new Department("Child") { Id = childDeptId };

        var sut = BuildSut(
            shareId, parentDeptId,
            (long)(FilePermission.ReadAll | WriteGroup),
            [childDept],
            out var ctx,
            descendantsOfShareDept: [childDeptId]);

        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.ListReadData),
            "Sub-department member should read the parent department's share.");
        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.CreateWriteData),
            "Sub-department member should write to the parent department's share.");
    }

    [Fact]
    public async Task DeptDefault_ShareOwnedBySubDept_AppliesSubDeptOwnDefault()
    {
        // Share belongs to the CHILD department, which defines its OWN read-only
        // default. The parent has Read+Write but must NOT widen the child's share.
        var shareId = Guid.NewGuid();
        var childDeptId = Guid.NewGuid();

        var childDept = new Department("Child")
        { Id = childDeptId, DefaultFilePermission = (long)FilePermission.ReadAll };

        var sut = BuildSut(
            shareId, childDeptId,
            (long)FilePermission.ReadAll,
            [childDept],
            out var ctx,
            departmentsById: [childDept]);

        Assert.True(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.ListReadData));
        Assert.False(await sut.HasAccessAsync(ctx, shareId, "", true, FilePermission.CreateWriteData),
            "Child department's own read-only default must apply to its own share.");
    }
}
