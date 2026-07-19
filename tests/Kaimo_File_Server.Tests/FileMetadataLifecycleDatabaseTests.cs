using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileMetadataLifecycleDatabaseTests : DatabaseTestBase
{
    [Fact]
    public async Task DeleteAclAsync_RemovesMetadataAndAclForCompleteSubtree()
    {
        var owner = SeedUser("owner");
        var share = SeedShare("docs");
        var acl = new AccessEntry(
            owner.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        SeedFileWithAcl(share.Id, "folder", true, owner.Id, acl);
        SeedFileWithAcl(share.Id, "folder/a.txt", false, owner.Id);
        SeedFileWithAcl(share.Id, "folder/nested/b.txt", false, owner.Id);
        SeedFileWithAcl(share.Id, "folder-other/keep.txt", false, owner.Id);

        await BuildAclService().DeleteAclAsync(share.Id, "folder");

        await using var db = NewContext();
        Assert.DoesNotContain(db.FileMetadata, m =>
            m.ShareId == share.Id && (m.Path == "folder" || m.Path.StartsWith("folder/")));
        Assert.Contains(db.FileMetadata, m => m.ShareId == share.Id && m.Path == "folder-other/keep.txt");
        Assert.False(await db.AccessEntries.AnyAsync(e => e.Id == acl.Id));
    }

    [Fact]
    public async Task RenamePath_ReplacesDestinationAndKeepsSourceOwnerAclAndName()
    {
        var sourceOwner = SeedUser("source-owner");
        var targetOwner = SeedUser("target-owner");
        var share = SeedShare("docs");
        var sourceAcl = new AccessEntry(
            sourceOwner.Id, AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);
        var source = SeedFileWithAcl(share.Id, "old.txt", false, sourceOwner.Id, sourceAcl);
        SeedFileWithAcl(share.Id, "new.txt", false, targetOwner.Id);

        await BuildAclService().RenameAclPathAsync(share.Id, "old.txt", "new.txt");

        await using var db = NewContext();
        var renamed = await db.FileMetadata.Include(m => m.Acl)
            .SingleAsync(m => m.ShareId == share.Id && m.Path == "new.txt");
        Assert.Equal(source.Id, renamed.Id);
        Assert.Equal(sourceOwner.Id, renamed.OwnerId);
        Assert.Equal("new.txt", renamed.Name);
        Assert.Contains(renamed.Acl, a => a.Id == sourceAcl.Id);
        Assert.DoesNotContain(db.FileMetadata, m => m.ShareId == share.Id && m.Path == "old.txt");
    }

    [Fact]
    public async Task DeleteShareMetadataAsync_RemovesOnlySelectedShare()
    {
        var owner = SeedUser("owner");
        var removedShare = SeedShare("removed");
        var keptShare = SeedShare("kept");
        SeedFileWithAcl(removedShare.Id, "folder/a.txt", false, owner.Id);
        SeedFileWithAcl(keptShare.Id, "folder/a.txt", false, owner.Id);

        await BuildAclService().DeleteShareMetadataAsync(removedShare.Id);

        await using var db = NewContext();
        Assert.DoesNotContain(db.FileMetadata, m => m.ShareId == removedShare.Id);
        Assert.Contains(db.FileMetadata, m => m.ShareId == keptShare.Id);
    }
}
