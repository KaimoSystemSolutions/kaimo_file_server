using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>How a rename merges the moved history into the history already at the destination.</summary>
public class VersionHistoryMergeTests
{
    private static readonly Guid Share = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = T0.AddDays(1);

    private static FileVersion V(string path, int minute, int number, string hash = "")
        => new(Share, path, T0.AddMinutes(minute), $"AA/BB/{(hash.Length > 0 ? hash : Guid.NewGuid().ToString("N"))}.bin.gz",
            hash.Length > 0 ? hash : Guid.NewGuid().ToString("N"), 10, "u", number);

    [Fact]
    public void Plan_NoDestinationHistory_MovesPathsAndKeepsNumbers()
    {
        var a = V("old.txt", 1, 1);
        var b = V("old.txt", 2, 2);

        var plan = VersionHistoryMerge.Plan("old.txt", "new.txt", [a, b], [], Now);

        Assert.Equal("new.txt", plan.NewPaths[a.Id]);
        Assert.Equal("new.txt", plan.NewPaths[b.Id]);
        Assert.Empty(plan.Displaced);
        Assert.Empty(plan.VersionNumbers);
        Assert.Empty(plan.LiveCopies);
    }

    [Fact]
    public void Plan_ReplaceRename_KeepsDestinationHistoryAndRenumbersByTime()
    {
        // Safe-save: the document's history plus the freshly written temp file on top.
        var doc1 = V("doc.odt", 1, 1);
        var doc2 = V("doc.odt", 2, 2);
        var temp = V("lu1.tmp", 3, 1);

        var plan = VersionHistoryMerge.Plan("lu1.tmp", "doc.odt", [temp], [doc1, doc2], Now);

        Assert.Equal("doc.odt", plan.NewPaths[temp.Id]);
        Assert.Empty(plan.Displaced);
        Assert.Equal(1, plan.VersionNumbers[doc1.Id]);
        Assert.Equal(2, plan.VersionNumbers[doc2.Id]);
        Assert.Equal(3, plan.VersionNumbers[temp.Id]);
        Assert.Empty(plan.LiveCopies);
    }

    [Fact]
    public void Plan_SameSecondAtDestination_DisplacesOnlyThatRow()
    {
        // One row per file and second: the moved row wins, it carries the live lineage.
        var doc1 = V("doc.odt", 1, 1);
        var doc2 = V("doc.odt", 3, 2);
        var temp = V("lu1.tmp", 3, 1);

        var plan = VersionHistoryMerge.Plan("lu1.tmp", "doc.odt", [temp], [doc1, doc2], Now);

        Assert.Equal(doc2.Id, Assert.Single(plan.Displaced).Id);
        Assert.Equal(1, plan.VersionNumbers[doc1.Id]);
        Assert.Equal(2, plan.VersionNumbers[temp.Id]);
        Assert.False(plan.VersionNumbers.ContainsKey(doc2.Id));
    }

    [Fact]
    public void Plan_DestinationNewerThanSource_AddsLiveCopyAsNewest()
    {
        // An older file renamed over a newer one: retention protects the newest row, which
        // must hold the content that is live now.
        var older = V("a.txt", 1, 1, "AAAA");
        var newer = V("b.txt", 5, 1, "BBBB");

        var plan = VersionHistoryMerge.Plan("a.txt", "b.txt", [older], [newer], Now);

        var copy = Assert.Single(plan.LiveCopies);
        Assert.Equal("b.txt", copy.FilePath);
        Assert.Equal(Share, copy.ShareId);
        Assert.Equal(older.StoragePath, copy.StoragePath);
        Assert.Equal(older.ContentHash, copy.ContentHash);
        Assert.Equal(older.Size, copy.Size);
        Assert.Equal(Now, copy.SnapshotTimestampUtc);
        Assert.Equal(3, copy.VersionNumber);
        Assert.Equal(1, plan.VersionNumbers[older.Id]);
        Assert.Equal(2, plan.VersionNumbers[newer.Id]);
    }

    [Fact]
    public void Plan_LiveCopy_IsNewerThanDestinationRowsFromTheFuture()
    {
        var older = V("a.txt", 1, 1);
        var future = new FileVersion(Share, "b.txt", Now.AddMinutes(5), "AA/BB/F.bin.gz", "F", 1, null, 1);

        var plan = VersionHistoryMerge.Plan("a.txt", "b.txt", [older], [future], Now);

        Assert.Equal(Now.AddMinutes(5).AddSeconds(1), Assert.Single(plan.LiveCopies).SnapshotTimestampUtc);
    }

    [Fact]
    public void Plan_LiveCopy_TruncatesNowToSeconds()
    {
        var older = V("a.txt", 1, 1);
        var newer = V("b.txt", 5, 1);

        var plan = VersionHistoryMerge.Plan("a.txt", "b.txt", [older], [newer], Now.AddMilliseconds(750));

        Assert.Equal(Now, Assert.Single(plan.LiveCopies).SnapshotTimestampUtc);
    }

    [Fact]
    public void Plan_DirectoryRename_MergesPerFileAndLeavesOtherDestinationRowsAlone()
    {
        var movedA = V("old/a.txt", 2, 1);
        var movedB = V("old/sub/b.txt", 3, 1);
        var destA = V("new/a.txt", 1, 1);
        var destOther = V("new/other.txt", 1, 4);

        var plan = VersionHistoryMerge.Plan("old", "new", [movedA, movedB], [destA, destOther], Now);

        Assert.Equal("new/a.txt", plan.NewPaths[movedA.Id]);
        Assert.Equal("new/sub/b.txt", plan.NewPaths[movedB.Id]);
        Assert.Empty(plan.Displaced);
        Assert.Equal(1, plan.VersionNumbers[destA.Id]);
        Assert.Equal(2, plan.VersionNumbers[movedA.Id]);
        Assert.False(plan.VersionNumbers.ContainsKey(destOther.Id));
        Assert.False(plan.VersionNumbers.ContainsKey(movedB.Id));
    }

    [Fact]
    public void Plan_NoSourceHistory_LeavesDestinationUntouched()
    {
        // An unversioned file (excluded, too large) renamed over a versioned one: the
        // replaced content stays recoverable.
        var dest = V("doc.txt", 1, 1);

        var plan = VersionHistoryMerge.Plan("big.iso", "doc.txt", [], [dest], Now);

        Assert.Empty(plan.NewPaths);
        Assert.Empty(plan.Displaced);
        Assert.Empty(plan.VersionNumbers);
        Assert.Empty(plan.LiveCopies);
    }
}
