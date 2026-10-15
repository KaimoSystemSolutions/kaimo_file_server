using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Sync;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A device syncs a share subtree by path. When that folder (or an ancestor) is renamed, the sync
/// endpoints answer 409 <c>sync_root_renamed</c> with a rename id, and <c>renames/{id}</c> tells the
/// client where the folder went. Runs the real change-log repository (SQLite) and real file services
/// over a temp directory; only ACLs are mocked.
/// </summary>
public sealed class SyncRootRenameTests : DatabaseTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-syncroot-" + Guid.NewGuid().ToString("N"));
    private readonly ShareDefinition _share;
    private readonly FileChangeLogRepository _log;
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);
    private readonly SyncApiController _sut;

    public SyncRootRenameTests()
    {
        Directory.CreateDirectory(_root);
        _share = new ShareDefinition("docs", _root);
        _log = new FileChangeLogRepository(DbFactory);

        var acl = new Mock<IAclService>();
        acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), _share.Id, It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, string path, bool _, FilePermission _) => !_denied.Contains(path));
        acl.Setup(a => a.HasAccessBatchAsync(It.IsAny<UserContext>(), _share.Id,
                It.IsAny<IReadOnlyList<(string, bool)>>(), It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool _)> items, FilePermission _) =>
                items.ToDictionary(i => i.Path, i => !_denied.Contains(i.Path)));

        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_user.User.Id)).ReturnsAsync(_user);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_share.Id)).ReturnsAsync(_share);
        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(_share))
            .Returns(() => new FileService(new FileSystemStorage(_root, _share.Id, null), acl.Object, null, _share.Id));

        _sut = new SyncApiController(
            users.Object, Mock.Of<ISyncDeviceRepository>(), Mock.Of<IDeviceSyncProfileRepository>(),
            shares.Object, factory.Object, Mock.Of<ISyncQueryService>(),
            Mock.Of<IFileChangeCursorRepository>(), _log)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, _user.User.Id.ToString()) }, "test"))
                }
            }
        };
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Dir(string rel) => Directory.CreateDirectory(Path.Combine(_root, rel));

    private async Task<FileChangeLogEntry> Renamed(string from, string to)
    {
        var e = new FileChangeLogEntry
        {
            ShareId = _share.Id, ChangeType = FileChangeType.Renamed, Path = to, OldPath = from,
            IsDirectory = true, CreatedAtUtc = DateTime.UtcNow,
        };
        await _log.AppendAsync(e);
        return e;
    }

    private static (int Status, object? Value) Unwrap(IActionResult r)
    {
        var o = Assert.IsAssignableFrom<ObjectResult>(r);
        return (o.StatusCode ?? 200, o.Value);
    }

    // ─────────────────── pure path rewrite ───────────────────

    [Theory]
    [InlineData("a/b", "a/b", "x", "x")]
    [InlineData("a/b", "a", "x", "x/b")]
    [InlineData("a/b/c", "a/b", "m/n", "m/n/c")]
    public void Rewrite_maps_the_path_through_a_rename(string path, string oldPath, string newPath, string expected)
        => Assert.Equal(expected, SyncRootRelocation.Rewrite(path, oldPath, newPath));

    [Fact]
    public async Task Resolve_follows_a_rename_chain_and_stops_at_the_hop_limit()
    {
        var first = await Renamed("a", "x");
        await Renamed("x/b", "y");

        var move = await SyncRootRelocation.ResolveAsync(_log, _share.Id, "a/b", 0, newest: false);

        Assert.Equal(first.Seq, move!.RenameId);
        Assert.Equal("y", move.NewPath);

        // A ping-pong chain is cut off instead of looping.
        for (int i = 0; i < SyncRootRelocation.MaxHops + 5; i++)
            await Renamed(i % 2 == 0 ? "y" : "z", i % 2 == 0 ? "z" : "y");
        Assert.NotNull(await SyncRootRelocation.ResolveAsync(_log, _share.Id, "a/b", 0, newest: false));
    }

    // ─────────────────── delta / changes ───────────────────

    [Fact]
    public async Task Changes_after_an_ancestor_rename_is_409_with_the_rename_id()
    {
        Dir("x/b");
        var rename = await Renamed("a", "x");

        var (status, value) = Unwrap(await _sut.Changes(_share.Id, 0, "a/b", null));

        Assert.Equal(409, status);
        var error = Assert.IsType<SyncRootRenamedError>(value);
        Assert.Equal("sync_root_renamed", error.Code);
        Assert.Equal(rename.Seq, error.RenameId);
    }

    [Fact]
    public async Task Delta_after_the_root_itself_was_renamed_is_409()
    {
        Dir("a/c");
        var rename = await Renamed("a/b", "a/c");

        var (status, value) = Unwrap(await _sut.Delta(_share.Id, "a/b"));

        Assert.Equal(409, status);
        Assert.Equal(rename.Seq, Assert.IsType<SyncRootRenamedError>(value).RenameId);
    }

    [Fact]
    public async Task Delta_without_cursor_follows_the_root_rename_not_a_later_ancestor_rename()
    {
        // x/a → x/b, then x → y: the newest rename covering x/a (x → y) alone maps it to y/a,
        // which does not exist; the root's own rename leads to y/b.
        Dir("y/b");
        var rename = await Renamed("x/a", "x/b");
        await Renamed("x", "y");

        var (status, value) = Unwrap(await _sut.Delta(_share.Id, "x/a"));

        Assert.Equal(409, status);
        var id = Assert.IsType<SyncRootRenamedError>(value).RenameId;
        Assert.Equal(rename.Seq, id);
        var dto = Assert.IsType<SyncRootRenameDto>(Unwrap(await _sut.RootRename(_share.Id, id, "x/a")).Value);
        Assert.Equal("y/b", dto.NewPath);
    }

    [Fact]
    public async Task Changes_ignores_a_rename_the_client_already_passed()
    {
        Dir("x/b");
        var rename = await Renamed("a", "x");

        var (status, value) = Unwrap(await _sut.Changes(_share.Id, rename.Seq, "a/b", null));

        Assert.Equal(404, status);
        Assert.Equal("sync_root_missing", Assert.IsType<ApiError>(value).Code);
    }

    [Fact]
    public async Task Deleted_root_is_404_sync_root_missing_not_500()
    {
        var (status, value) = Unwrap(await _sut.Delta(_share.Id, "gone"));

        Assert.Equal(404, status);
        Assert.Equal("sync_root_missing", Assert.IsType<ApiError>(value).Code);
    }

    [Fact]
    public async Task Root_deleted_into_the_recycle_bin_is_404_sync_root_missing_not_a_rename()
    {
        // With the recycle bin on, a delete is logged as a rename into .RECYCLE_BIN; the client must
        // not re-point its connection into the bin.
        Dir(".RECYCLE_BIN/a/b");
        var delete = await Renamed("a", ".RECYCLE_BIN/a");

        var (status, value) = Unwrap(await _sut.Changes(_share.Id, 0, "a/b", null));

        Assert.Equal(404, status);
        Assert.Equal("sync_root_missing", Assert.IsType<ApiError>(value).Code);
        Assert.Equal(404, Unwrap(await _sut.Delta(_share.Id, "a/b")).Status);
        Assert.Equal(404, Unwrap(await _sut.RootRename(_share.Id, delete.Seq, "a/b")).Status);
    }

    [Fact]
    public async Task Root_renamed_and_then_deleted_into_the_recycle_bin_is_not_resolved_into_the_bin()
    {
        Dir(".RECYCLE_BIN/x/b");
        var rename = await Renamed("a", "x");
        await Renamed("x", ".RECYCLE_BIN/x");

        Assert.Equal(404, Unwrap(await _sut.RootRename(_share.Id, rename.Seq, "a/b")).Status);
    }

    [Fact]
    public async Task Root_renamed_away_and_back_syncs_normally()
    {
        Dir("a/b");
        await Renamed("a", "x");
        await Renamed("x", "a");

        var (status, value) = Unwrap(await _sut.Changes(_share.Id, 0, "a/b", null));

        Assert.Equal(200, status);
        Assert.IsType<ChangesFeedDto>(value);
    }

    [Fact]
    public async Task Rename_to_a_folder_the_caller_cannot_list_is_not_revealed()
    {
        Dir("secret/b");
        await Renamed("a", "secret");
        _denied.Add("secret/b");

        var (status, value) = Unwrap(await _sut.Changes(_share.Id, 0, "a/b", null));

        Assert.Equal(404, status);
        Assert.Equal("sync_root_missing", Assert.IsType<ApiError>(value).Code);
    }

    // ─────────────────── renames/{id} ───────────────────

    [Fact]
    public async Task RootRename_returns_the_final_path_of_the_chain()
    {
        Dir("y/b");
        var first = await Renamed("a", "x");
        await Renamed("x", "y");

        var (status, value) = Unwrap(await _sut.RootRename(_share.Id, first.Seq, "a/b"));

        Assert.Equal(200, status);
        var dto = Assert.IsType<SyncRootRenameDto>(value);
        Assert.Equal(first.Seq, dto.RenameId);
        Assert.Equal("a/b", dto.OldPath);
        Assert.Equal("y/b", dto.NewPath);
    }

    [Fact]
    public async Task RootRename_with_an_id_that_does_not_cover_the_path_is_404()
    {
        Dir("x");
        var other = await Renamed("q", "x");

        Assert.Equal(404, Unwrap(await _sut.RootRename(_share.Id, other.Seq, "a/b")).Status);
        Assert.Equal(404, Unwrap(await _sut.RootRename(_share.Id, 999_999, "a/b")).Status);
    }

    [Fact]
    public async Task RootRename_to_an_unlistable_folder_is_403()
    {
        Dir("x/b");
        var rename = await Renamed("a", "x");
        _denied.Add("x/b");

        Assert.Equal(403, Unwrap(await _sut.RootRename(_share.Id, rename.Seq, "a/b")).Status);
    }
}
