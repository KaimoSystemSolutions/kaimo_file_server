using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;
using Xunit;

namespace Kaimo_File_Server_Core.Tests;

public class DomainModelTests
{
    // ========== User ==========

    [Fact]
    public void User_Constructor_SetsAllProperties()
    {
        var id = Guid.NewGuid();
        var user = new User(id, "Marco Hanisch", "marco.hanisch", "bcrypt_hash", "nt_hash");

        Assert.Equal(id, user.Id);
        Assert.Equal("Marco Hanisch", user.Name);
        Assert.Equal("marco.hanisch", user.Username);
        Assert.Equal("bcrypt_hash", user.PasswordHash);
        Assert.Equal("nt_hash", user.NtHash);
    }

    // ========== Group ==========

    [Fact]
    public void Group_Constructor_SetsIdAndName()
    {
        var id = Guid.NewGuid();
        var group = new Group(id, "Admins");

        Assert.Equal(id, group.Id);
        Assert.Equal("Admins", group.Name);
    }

    // ========== Role ==========

    [Fact]
    public void Role_Constructor_SetsIdAndName()
    {
        var id = Guid.NewGuid();
        var role = new Role(id, "Administrator");

        Assert.Equal(id, role.Id);
        Assert.Equal("Administrator", role.Name);
    }

    // ========== UserGroup ==========

    [Fact]
    public void UserGroup_Constructor_SetsBothIds()
    {
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var ug = new UserGroup(userId, groupId);

        Assert.Equal(userId, ug.UserId);
        Assert.Equal(groupId, ug.GroupId);
    }

    // ========== UserRole ==========

    [Fact]
    public void UserRole_Constructor_SetsBothIds()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var ur = new UserRole(userId, roleId);

        Assert.Equal(userId, ur.UserId);
        Assert.Equal(roleId, ur.RoleId);
    }

    // ========== UserContext ==========

    [Fact]
    public void UserContext_Constructor_SetsAllCollections()
    {
        var user = new User(Guid.NewGuid(), "Test", "test", "h", "n");
        var groups = new HashSet<Group> { new(Guid.NewGuid(), "G1") };
        var roles = new HashSet<Role> { new(Guid.NewGuid(), "R1") };
        var perms = new HashSet<string> { "file.read", "file.write" };

        var ctx = new UserContext(user, groups, roles, perms);

        Assert.Same(user, ctx.User);
        Assert.Single(ctx.Groups);
        Assert.Single(ctx.Roles);
        Assert.Equal(2, ctx.Permissions.Count);
    }

    // ========== ShareDefinition ==========

    [Fact]
    public void ShareDefinition_Constructor_SetsDefaults()
    {
        var share = new ShareDefinition("projekte", "/data/storage/projekte");

        Assert.NotEqual(Guid.Empty, share.Id);
        Assert.Equal("projekte", share.Name);
        Assert.Equal("/data/storage/projekte", share.Path);
        Assert.True(share.IsEnabled);
    }

    [Fact]
    public void ShareDefinition_Constructor_CanBeDisabled()
    {
        var share = new ShareDefinition("archiv", "/data/storage/archiv", isEnabled: false);
        Assert.False(share.IsEnabled);
    }

    // ========== ShareAccessEntry ==========

    [Fact]
    public void ShareAccessEntry_Constructor_SetsValues()
    {
        var principalId = Guid.NewGuid();
        var entry = new ShareAccessEntry("test", principalId);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("test", entry.ShareName);
        Assert.Equal(principalId, entry.PrincipalId);
    }

    // ========== AccessEntry ==========

    [Fact]
    public void AccessEntry_Constructor_SetsValues()
    {
        var principalId = Guid.NewGuid();
        var entry = new AccessEntry(
            principalId,
            AclEntryType.Allow,
            FilePermission.ReadAll,
            AclInheritance.Everything);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(principalId, entry.PrincipalId);
        Assert.Equal(AclEntryType.Allow, entry.EntryType);
        Assert.Equal(FilePermission.ReadAll, entry.Permissions);
        Assert.Equal(AclInheritance.Everything, entry.Inheritance);
    }

    // ========== FilePermission Flags ==========

    [Fact]
    public void FilePermission_ReadAll_ContainsAllReadBits()
    {
        var readAll = FilePermission.ReadAll;
        Assert.True((readAll & FilePermission.TraverseExecute) != 0);
        Assert.True((readAll & FilePermission.ListReadData) != 0);
        Assert.True((readAll & FilePermission.ReadAttributes) != 0);
        Assert.True((readAll & FilePermission.ReadExtAttributes) != 0);
        Assert.True((readAll & FilePermission.ReadPermissions) != 0);
        // Kein Write-Bit enthalten
        Assert.True((readAll & FilePermission.CreateWriteData) == 0);
    }

    [Fact]
    public void FilePermission_WriteAll_ContainsAllWriteBits()
    {
        var writeAll = FilePermission.WriteAll;
        Assert.True((writeAll & FilePermission.CreateWriteData) != 0);
        Assert.True((writeAll & FilePermission.CreateAppendData) != 0);
        Assert.True((writeAll & FilePermission.WriteAttributes) != 0);
        Assert.True((writeAll & FilePermission.WriteExtAttributes) != 0);
        Assert.True((writeAll & FilePermission.DeleteSubItems) != 0);
        Assert.True((writeAll & FilePermission.Delete) != 0);
        // Kein Read-Bit enthalten
        Assert.True((writeAll & FilePermission.ListReadData) == 0);
    }

    [Fact]
    public void FilePermission_FullControl_ContainsEverything()
    {
        var full = FilePermission.FullControl;
        Assert.Equal(FilePermission.ReadAll | FilePermission.WriteAll | FilePermission.AdminAll, full);
    }

    // ========== AclInheritance Flags ==========

    [Fact]
    public void AclInheritance_Everything_ContainsAllBits()
    {
        var everything = AclInheritance.Everything;
        Assert.True((everything & AclInheritance.ThisFolder) != 0);
        Assert.True((everything & AclInheritance.SubFolders) != 0);
        Assert.True((everything & AclInheritance.SubFiles) != 0);
        Assert.True((everything & AclInheritance.AllDescendants) != 0);
    }

    [Fact]
    public void AclInheritance_ThisAndDirect_DoesNotIncludeAllDescendants()
    {
        var thisAndDirect = AclInheritance.ThisAndDirect;
        Assert.True((thisAndDirect & AclInheritance.ThisFolder) != 0);
        Assert.True((thisAndDirect & AclInheritance.SubFolders) != 0);
        Assert.True((thisAndDirect & AclInheritance.SubFiles) != 0);
        Assert.True((thisAndDirect & AclInheritance.AllDescendants) == 0);
    }
}
