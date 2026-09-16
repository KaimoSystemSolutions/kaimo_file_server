using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Semantic counterpart to <see cref="PathPrefixQueryTranslationTests"/>: exercises the
/// real <c>DeleteFileMetadataPathsAsync</c> subtree delete and asserts that a LIKE
/// metacharacter in a folder name (<c>_</c>, <c>%</c>) does NOT match an unrelated
/// sibling.
///
/// Runs against the harness database (SQLite). SQLite's <c>StartsWith</c> translation
/// is its own; a green result here proves the SQLite path is safe, while
/// <see cref="PathPrefixQueryTranslationTests"/> covers Npgsql. Both are kept as
/// permanent regression guards.
/// </summary>
public class PathPrefixQueryBehaviourTests : DatabaseTestBase
{
    [Fact]
    public async Task DeleteFileMetadataPaths_UnderscoreInFolderName_DoesNotMatchSiblingWithAnyCharacter()
    {
        var owner = SeedUser("owner");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "report_2024/a.txt", false, owner.Id);
        SeedFileWithAcl(share.Id, "reportX2024/a.txt", false, owner.Id);

        var deleted = await AclRepo().DeleteFileMetadataPathsAsync(share.Id, "report_2024");

        Assert.Equal(1, deleted);
        await using var db = NewContext();
        Assert.DoesNotContain(db.FileMetadata, m => m.ShareId == share.Id && m.Path.StartsWith("report_2024"));
        Assert.Contains(db.FileMetadata, m => m.ShareId == share.Id && m.Path == "reportX2024/a.txt");
    }

    [Fact]
    public async Task DeleteFileMetadataPaths_PercentInFolderName_DoesNotMatchUnrelatedSubtree()
    {
        var owner = SeedUser("owner");
        var share = SeedShare("docs");
        SeedFileWithAcl(share.Id, "50%_done/a.txt", false, owner.Id);
        SeedFileWithAcl(share.Id, "50-percent-done/a.txt", false, owner.Id);

        var deleted = await AclRepo().DeleteFileMetadataPathsAsync(share.Id, "50%_done");

        Assert.Equal(1, deleted);
        await using var db = NewContext();
        Assert.DoesNotContain(db.FileMetadata, m => m.ShareId == share.Id && m.Path.StartsWith("50%"));
        Assert.Contains(db.FileMetadata, m => m.ShareId == share.Id && m.Path == "50-percent-done/a.txt");
    }
}
