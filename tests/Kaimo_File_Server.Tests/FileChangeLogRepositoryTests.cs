using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Database-backed tests for <see cref="FileChangeLogRepository"/> — the append-only store behind
/// the client <c>change_seq</c> feed. Runs against the real EF schema on in-memory SQLite (via
/// <see cref="DatabaseTestBase"/>), so the identity sequence, the <c>Seq &gt; N</c> cursor scan, and
/// the subtree prefix filter behave as production PostgreSQL would.
/// </summary>
public sealed class FileChangeLogRepositoryTests : DatabaseTestBase
{
    private FileChangeLogRepository Repo() => new(DbFactory);

    private static FileChangeLogEntry Entry(
        Guid shareId, FileChangeType type, string path, string? oldPath = null, bool isDir = false)
        => new()
        {
            ShareId = shareId,
            ChangeType = type,
            Path = path,
            OldPath = oldPath,
            IsDirectory = isDir,
            CreatedAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task AppendAsync_AssignsAscendingMonotonicSeq()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        var a = Entry(share, FileChangeType.Created, "a.txt");
        var b = Entry(share, FileChangeType.Created, "b.txt");
        await repo.AppendAsync(a);
        await repo.AppendAsync(b);

        Assert.True(a.Seq > 0);
        Assert.True(b.Seq > a.Seq);
    }

    [Fact]
    public async Task GetChangesSinceAsync_ReturnsOnlyGreaterSeq_InOrder()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        var a = Entry(share, FileChangeType.Created, "a.txt");
        var b = Entry(share, FileChangeType.Modified, "b.txt");
        var c = Entry(share, FileChangeType.Deleted, "c.txt");
        await repo.AppendAsync(a);
        await repo.AppendAsync(b);
        await repo.AppendAsync(c);

        var since = await repo.GetChangesSinceAsync(share, a.Seq, null, 10);

        Assert.Equal(new[] { b.Seq, c.Seq }, since.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public async Task GetChangesSinceAsync_ScopesToSubtree_AndSurfacesRenameLeavingViaOldPath()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        await repo.AppendAsync(Entry(share, FileChangeType.Created, "docs/a.txt"));
        await repo.AppendAsync(Entry(share, FileChangeType.Created, "docs2/sibling.txt")); // must NOT match "docs"
        await repo.AppendAsync(Entry(share, FileChangeType.Created, "other/b.txt"));
        // A rename that MOVES a file out of the watched subtree must still surface (via OldPath).
        await repo.AppendAsync(Entry(share, FileChangeType.Renamed, "recycle/a.txt", oldPath: "docs/a.txt"));

        var underDocs = await repo.GetChangesSinceAsync(share, 0, "docs", 10);

        Assert.Equal(2, underDocs.Count);
        Assert.Contains(underDocs, e => e.Path == "docs/a.txt");
        Assert.Contains(underDocs, e => e.ChangeType == FileChangeType.Renamed && e.OldPath == "docs/a.txt");
        Assert.DoesNotContain(underDocs, e => e.Path == "docs2/sibling.txt");
        Assert.DoesNotContain(underDocs, e => e.Path == "other/b.txt");
    }

    [Fact]
    public async Task GetChangesSinceAsync_RespectsMaxCount()
    {
        var repo = Repo();
        var share = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
            await repo.AppendAsync(Entry(share, FileChangeType.Created, $"f{i}.txt"));

        var page = await repo.GetChangesSinceAsync(share, 0, null, 3);

        Assert.Equal(3, page.Count);
    }

    [Fact]
    public async Task GetHeadSeqAsync_ReturnsMax_OrZeroWhenEmpty()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        Assert.Equal(0, await repo.GetHeadSeqAsync(share, null));

        await repo.AppendAsync(Entry(share, FileChangeType.Created, "docs/a.txt"));
        var last = Entry(share, FileChangeType.Created, "docs/b.txt");
        await repo.AppendAsync(last);

        Assert.Equal(last.Seq, await repo.GetHeadSeqAsync(share, null));
        Assert.Equal(last.Seq, await repo.GetHeadSeqAsync(share, "docs"));
        // A subtree with no changes yet has head 0 — the rename case the coarse token missed still
        // advances the whole-share head, but an unrelated subtree stays put.
        Assert.Equal(0, await repo.GetHeadSeqAsync(share, "unrelated"));
    }

    [Fact]
    public async Task Queries_AreScopedToTheShare()
    {
        var repo = Repo();
        var shareA = Guid.NewGuid();
        var shareB = Guid.NewGuid();

        await repo.AppendAsync(Entry(shareA, FileChangeType.Created, "a.txt"));
        await repo.AppendAsync(Entry(shareB, FileChangeType.Created, "a.txt"));

        var a = await repo.GetChangesSinceAsync(shareA, 0, null, 10);
        Assert.Single(a);
        Assert.Equal(0, await repo.GetHeadSeqAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task PruneOlderThanAsync_RemovesOldEntriesOnly()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        var old = Entry(share, FileChangeType.Created, "old.txt");
        old.CreatedAtUtc = DateTime.UtcNow.AddDays(-10);
        var fresh = Entry(share, FileChangeType.Created, "fresh.txt");
        await repo.AppendAsync(old);
        await repo.AppendAsync(fresh);

        var removed = await repo.PruneOlderThanAsync(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(1, removed);
        var remaining = await repo.GetChangesSinceAsync(share, 0, null, 10);
        Assert.Single(remaining);
        Assert.Equal("fresh.txt", remaining[0].Path);
    }

    [Fact]
    public async Task GetOldestSeqAsync_TracksTheRetainedPrefix_AfterPruning()
    {
        var repo = Repo();
        var share = Guid.NewGuid();

        Assert.Equal(0, await repo.GetOldestSeqAsync()); // empty log

        var old = Entry(share, FileChangeType.Created, "old.txt");
        old.CreatedAtUtc = DateTime.UtcNow.AddDays(-10);
        var fresh = Entry(share, FileChangeType.Created, "fresh.txt");
        await repo.AppendAsync(old);
        await repo.AppendAsync(fresh);

        Assert.Equal(old.Seq, await repo.GetOldestSeqAsync());

        // Pruning the low-seq prefix advances the oldest retained seq — this is the value the
        // changes feed compares a client cursor against to detect a retention gap.
        await repo.PruneOlderThanAsync(DateTime.UtcNow.AddDays(-1));
        Assert.Equal(fresh.Seq, await repo.GetOldestSeqAsync());
    }
}
