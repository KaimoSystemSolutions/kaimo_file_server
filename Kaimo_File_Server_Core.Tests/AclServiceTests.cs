using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;
using Xunit;

namespace Kaimo_File_Server_Core.Tests;

public class AclServiceTests
{
    private readonly AclService _sut = new();

    // ========== Helper ==========

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
            Id = Guid.NewGuid(),
            Path = "/test/file.txt",
            Name = "file.txt",
            Size = 100,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            OwnerId = Guid.NewGuid().ToString(),
            Acl = acl ?? []
        };

    // ========== HasAccess — Grundverhalten ==========

    [Fact]
    public void HasAccess_NullUserContext_ReturnsFalse()
    {
        // Nach dem Refactoring: kein UserContext = kein Zugriff (deny by default)
        var file = CreateFile();
        var result = _sut.HasAccess(null!, file, FilePermission.ListReadData);
        Assert.False(result);
    }

    [Fact]
    public void HasAccess_EmptyAcl_ReturnsTrue()
    {
        var ctx = CreateContext();
        var file = CreateFile(acl: []);
        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.True(result);
    }

    [Fact]
    public void HasAccess_NullAcl_ReturnsTrue()
    {
        var ctx = CreateContext();
        var fileWithNullAcl = new FileMetadata
        {
            Id = Guid.NewGuid(),
            Path = "/test",
            Name = "test",
            Size = 0,
            IsDirectory = false,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = null!
        };
        var result = _sut.HasAccess(ctx, fileWithNullAcl, FilePermission.ListReadData);
        Assert.True(result);
    }

    // ========== HasAccess — Allow ==========

    [Fact]
    public void HasAccess_ExplicitAllow_ForUser_ReturnsTrue()
    {
        var user = CreateUser();
        var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.True(result);
    }

    [Fact]
    public void HasAccess_AllowViaGroup_ReturnsTrue()
    {
        var user = CreateUser();
        var group = new Group(Guid.NewGuid(), "Devs");
        var ctx = CreateContext(user, groups: [group]);
        var entry = new AccessEntry(group.Id, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.True(result);
    }

    [Fact]
    public void HasAccess_AllowViaRole_ReturnsTrue()
    {
        var user = CreateUser();
        var role = new Role(Guid.NewGuid(), "Admin");
        var ctx = CreateContext(user, roles: [role]);
        var entry = new AccessEntry(role.Id, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.True(result);
    }

    [Fact]
    public void HasAccess_NoMatchingPrincipal_ReturnsFalse()
    {
        var ctx = CreateContext();
        var otherPrincipal = Guid.NewGuid();
        var entry = new AccessEntry(otherPrincipal, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.False(result);
    }

    [Fact]
    public void HasAccess_WrongPermission_ReturnsFalse()
    {
        var user = CreateUser();
        var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.CreateWriteData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.False(result);
    }

    // ========== HasAccess — Deny hat Vorrang ==========

    [Fact]
    public void HasAccess_DenyOverridesAllow_ReturnsFalse()
    {
        var user = CreateUser();
        var ctx = CreateContext(user);
        var allow = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var deny = new AccessEntry(user.Id, AclEntryType.Deny,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [allow, deny]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.False(result);
    }

    [Fact]
    public void HasAccess_DenyViaGroup_OverridesUserAllow()
    {
        var user = CreateUser();
        var group = new Group(Guid.NewGuid(), "Restricted");
        var ctx = CreateContext(user, groups: [group]);

        var allow = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var deny = new AccessEntry(group.Id, AclEntryType.Deny,
            FilePermission.ListReadData, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [allow, deny]);

        var result = _sut.HasAccess(ctx, file, FilePermission.ListReadData);
        Assert.False(result);
    }

    // ========== HasAccess — Combined Permissions ==========

    [Fact]
    public void HasAccess_CombinedPermissions_PartialMatch()
    {
        var user = CreateUser();
        var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ListReadData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ReadAttributes));
        Assert.False(_sut.HasAccess(ctx, file, FilePermission.CreateWriteData));
    }

    [Fact]
    public void HasAccess_FullControl_AllowsEverything()
    {
        var user = CreateUser();
        var ctx = CreateContext(user);
        var entry = new AccessEntry(user.Id, AclEntryType.Allow,
            FilePermission.FullControl, AclInheritance.ThisOnly);
        var file = CreateFile(acl: [entry]);

        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ListReadData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.CreateWriteData));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.Delete));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.ChangePermissions));
        Assert.True(_sut.HasAccess(ctx, file, FilePermission.TakeOwnership));
    }

    // ========== GetEffectiveAcl — Vererbung ==========

    [Fact]
    public void GetEffectiveAcl_AllDescendants_AppliesToBoth()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.AllDescendants);

        var forDir = _sut.GetEffectiveAcl([entry], isDirectory: true);
        var forFile = _sut.GetEffectiveAcl([entry], isDirectory: false);

        Assert.Single(forDir);
        Assert.Single(forFile);
    }

    [Fact]
    public void GetEffectiveAcl_SubFoldersOnly_AppliesToDirectoryOnly()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.SubFolders);

        var forDir = _sut.GetEffectiveAcl([entry], isDirectory: true);
        var forFile = _sut.GetEffectiveAcl([entry], isDirectory: false);

        Assert.Single(forDir);
        Assert.Empty(forFile);
    }

    [Fact]
    public void GetEffectiveAcl_SubFilesOnly_AppliesToFileOnly()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.SubFiles);

        var forDir = _sut.GetEffectiveAcl([entry], isDirectory: true);
        var forFile = _sut.GetEffectiveAcl([entry], isDirectory: false);

        Assert.Empty(forDir);
        Assert.Single(forFile);
    }

    [Fact]
    public void GetEffectiveAcl_ThisFolderOnly_NeverInherited()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.ThisOnly);

        var forDir = _sut.GetEffectiveAcl([entry], isDirectory: true);
        var forFile = _sut.GetEffectiveAcl([entry], isDirectory: false);

        Assert.Empty(forDir);
        Assert.Empty(forFile);
    }

    [Fact]
    public void GetEffectiveAcl_Everything_AppliesToBoth()
    {
        var entry = new AccessEntry(Guid.NewGuid(), AclEntryType.Allow,
            FilePermission.ReadAll, AclInheritance.Everything);

        var forDir = _sut.GetEffectiveAcl([entry], isDirectory: true);
        var forFile = _sut.GetEffectiveAcl([entry], isDirectory: false);

        Assert.Single(forDir);
        Assert.Single(forFile);
    }

    [Fact]
    public void GetEffectiveAcl_MixedEntries_FiltersCorrectly()
    {
        var entries = new List<AccessEntry>
        {
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.SubFolders),
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.WriteAll, AclInheritance.SubFiles),
            new(Guid.NewGuid(), AclEntryType.Deny, FilePermission.Delete, AclInheritance.AllDescendants),
            new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.FullControl, AclInheritance.ThisOnly),
        };

        var forDir = _sut.GetEffectiveAcl(entries, isDirectory: true);
        var forFile = _sut.GetEffectiveAcl(entries, isDirectory: false);

        Assert.Equal(2, forDir.Count);
        Assert.Equal(2, forFile.Count);
    }
}