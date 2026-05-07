using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class AclServiceTests
{
    private readonly AclService _sut = new();

    private static User CreateUser(Guid? id = null)
        => new(id ?? Guid.NewGuid(), "Test User", "testuser", "hash", "nthash");

    private static UserContext CreateContext(User? user = null, HashSet<Group>? groups = null, HashSet<Role>? roles = null)
    {
        user ??= CreateUser();
        return new UserContext(user, groups ?? [], roles ?? [], []);
    }

    private static FileMetadata CreateFile(IReadOnlyList<AccessEntry>? acl = null)
        => new()
        {
            Id = Guid.NewGuid(), Path = "/test/file.txt", Name = "file.txt",
            Size = 100, IsDirectory = false, CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow, OwnerId = Guid.NewGuid().ToString(), Acl = acl ?? []
        };

    [Fact] public void HasAccess_NullUserContext_ReturnsFalse()
    { Assert.False(_sut.HasAccess(null!, CreateFile(), FilePermission.ListReadData)); }

    [Fact] public void HasAccess_EmptyAcl_ReturnsTrue()
    { Assert.True(_sut.HasAccess(CreateContext(), CreateFile(acl: []), FilePermission.ListReadData)); }

    [Fact] public void HasAccess_NullAcl_ReturnsTrue()
    {
        var file = new FileMetadata { Id = Guid.NewGuid(), Path = "/test", Name = "test", Size = 0, IsDirectory = false, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, Acl = null! };
        Assert.True(_sut.HasAccess(CreateContext(), file, FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_ExplicitAllow_ForUser_ReturnsTrue()
    {
        var user = CreateUser(); var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.True(_sut.HasAccess(ctx, CreateFile(acl: [entry]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_AllowViaGroup_ReturnsTrue()
    {
        var user = CreateUser(); var group = new Group(Guid.NewGuid(), "Devs");
        var ctx = CreateContext(user, groups: [group]);
        var entry = new AccessEntry(group.Id, AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.True(_sut.HasAccess(ctx, CreateFile(acl: [entry]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_AllowViaRole_ReturnsTrue()
    {
        var user = CreateUser(); var role = new Role(Guid.NewGuid(), "Admin");
        var ctx = CreateContext(user, roles: [role]);
        var entry = new AccessEntry(role.Id, AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.True(_sut.HasAccess(ctx, CreateFile(acl: [entry]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_NoMatchingPrincipal_ReturnsFalse()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.False(_sut.HasAccess(CreateContext(), CreateFile(acl: [entry]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_WrongPermission_ReturnsFalse()
    {
        var user = CreateUser(); var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.CreateWriteData, AclInheritance.ThisOnly);
        Assert.False(_sut.HasAccess(ctx, CreateFile(acl: [entry]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_DenyOverridesAllow_ReturnsFalse()
    {
        var user = CreateUser(); var ctx = CreateContext(user);
        var allow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        var deny = new AccessEntry(user.Id, AclEntryType.Deny, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.False(_sut.HasAccess(ctx, CreateFile(acl: [allow, deny]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_DenyViaGroup_OverridesUserAllow()
    {
        var user = CreateUser(); var group = new Group(Guid.NewGuid(), "Restricted");
        var ctx = CreateContext(user, groups: [group]);
        var allow = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ListReadData, AclInheritance.ThisOnly);
        var deny = new AccessEntry(group.Id, AclEntryType.Deny, FilePermission.ListReadData, AclInheritance.ThisOnly);
        Assert.False(_sut.HasAccess(ctx, CreateFile(acl: [allow, deny]), FilePermission.ListReadData));
    }

    [Fact] public void HasAccess_CombinedPermissions_PartialMatch()
    {
        var user = CreateUser(); var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ListReadData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ReadAttributes));
        Assert.False(_sut.HasAccess(ctx, file, FilePermission.CreateWriteData));
    }

    [Fact] public void HasAccess_FullControl_AllowsEverything()
    {
        var user = CreateUser(); var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow, FilePermission.FullControl, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ListReadData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.CreateWriteData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.Delete));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ChangePermissions));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.TakeOwnership));
    }

    [Fact] public void GetEffectiveAcl_AllDescendants_AppliesToBoth()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.AllDescendants);
        Assert.Single(_sut.GetEffectiveAcl([entry], isDirectory: true));
        Assert.Single(_sut.GetEffectiveAcl([entry], isDirectory: false));
    }

    [Fact] public void GetEffectiveAcl_SubFoldersOnly_AppliesToDirectoryOnly()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.SubFolders);
        Assert.Single(_sut.GetEffectiveAcl([entry], isDirectory: true));
        Assert.Empty(_sut.GetEffectiveAcl([entry], isDirectory: false));
    }

    [Fact] public void GetEffectiveAcl_SubFilesOnly_AppliesToFileOnly()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.SubFiles);
        Assert.Empty(_sut.GetEffectiveAcl([entry], isDirectory: true));
        Assert.Single(_sut.GetEffectiveAcl([entry], isDirectory: false));
    }

    [Fact] public void GetEffectiveAcl_ThisFolderOnly_NeverInherited()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.ThisOnly);
        Assert.Empty(_sut.GetEffectiveAcl([entry], isDirectory: true));
        Assert.Empty(_sut.GetEffectiveAcl([entry], isDirectory: false));
    }

    [Fact] public void GetEffectiveAcl_MixedEntries_FiltersCorrectly()
    {
        var entries = new List<AccessEntry>
        {
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.SubFolders),
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.WriteAll, AclInheritance.SubFiles),
            new(Guid.NewGuid(), AclEntryType.Deny, FilePermission.Delete, AclInheritance.AllDescendants),
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.FullControl, AclInheritance.ThisOnly),
        };
        Assert.Equal(2, _sut.GetEffectiveAcl(entries, isDirectory: true).Count);
        Assert.Equal(2, _sut.GetEffectiveAcl(entries, isDirectory: false).Count);
    }
}
