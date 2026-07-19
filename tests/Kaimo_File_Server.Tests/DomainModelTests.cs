using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class DomainModelTests
{
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

    [Fact] public void Group_Constructor_SetsIdAndName() { var id = Guid.NewGuid(); var g = new Group(id, "Admins"); Assert.Equal(id, g.Id); Assert.Equal("Admins", g.Name); }
    [Fact] public void Role_Constructor_SetsIdAndName() { var id = Guid.NewGuid(); var r = new Role(id, "Administrator"); Assert.Equal(id, r.Id); Assert.Equal("Administrator", r.Name); }
    [Fact] public void UserGroup_Constructor_SetsBothIds() { var u = Guid.NewGuid(); var g = Guid.NewGuid(); var ug = new UserGroup(u, g); Assert.Equal(u, ug.UserId); Assert.Equal(g, ug.GroupId); }

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

    [Fact] public void ShareDefinition_Constructor_SetsDefaults() { var s = new ShareDefinition("projekte", "/data/storage/projekte"); Assert.NotEqual(Guid.Empty, s.Id); Assert.Equal("projekte", s.Name); Assert.True(s.IsEnabled); }
    [Fact] public void ShareDefinition_Constructor_CanBeDisabled() { Assert.False(new ShareDefinition("archiv", "/data/storage/archiv", isEnabled: false).IsEnabled); }
    [Fact]
    public void AccessEntry_Constructor_SetsValues()
    {
        var p = Guid.NewGuid();
        var e = new AccessEntry(p, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        Assert.NotEqual(Guid.Empty, e.Id);
        Assert.Equal(p, e.PrincipalId);
        Assert.Equal(AclEntryType.Allow, e.EntryType);
        Assert.Equal(FilePermission.ReadAll, e.Permissions);
        Assert.Equal(AclInheritance.Everything, e.Inheritance);
    }

    [Fact]
    public void FilePermission_ReadAll_ContainsAllReadBits()
    {
        var r = FilePermission.ReadAll;
        Assert.True((r & FilePermission.TraverseExecute) != 0);
        Assert.True((r & FilePermission.ListReadData) != 0);
        Assert.True((r & FilePermission.ReadAttributes) != 0);
        Assert.True((r & FilePermission.ReadExtAttributes) != 0);
        Assert.True((r & FilePermission.ReadPermissions) != 0);
        Assert.True((r & FilePermission.CreateWriteData) == 0);
    }

    [Fact]
    public void FilePermission_WriteAll_ContainsAllWriteBits()
    {
        var w = FilePermission.WriteAll;
        Assert.True((w & FilePermission.CreateWriteData) != 0);
        Assert.True((w & FilePermission.Delete) != 0);
        Assert.True((w & FilePermission.ListReadData) == 0);
    }

    [Fact]
    public void FilePermission_FullControl_ContainsEverything()
    {
        Assert.Equal(FilePermission.ReadAll | FilePermission.WriteAll | FilePermission.AdminAll, FilePermission.FullControl);
    }

    [Fact]
    public void AclInheritance_Everything_ContainsAllBits()
    {
        var e = AclInheritance.Everything;
        Assert.True((e & AclInheritance.ThisFolder) != 0);
        Assert.True((e & AclInheritance.SubFolders) != 0);
        Assert.True((e & AclInheritance.SubFiles) != 0);
        Assert.True((e & AclInheritance.AllDescendants) != 0);
    }

    [Fact]
    public void AclInheritance_ThisAndDirect_DoesNotIncludeAllDescendants()
    {
        var t = AclInheritance.ThisAndDirect;
        Assert.True((t & AclInheritance.ThisFolder) != 0);
        Assert.True((t & AclInheritance.AllDescendants) == 0);
    }
}