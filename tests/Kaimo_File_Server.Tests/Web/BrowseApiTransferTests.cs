using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Copy / move / size endpoints of the client browse API, run against real file services over
/// temp directories (ACLs allow everything; authorization is the file service's job and covered
/// elsewhere). Two shares let the cross-share paths be exercised.
/// </summary>
public sealed class BrowseApiTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly ShareDefinition _a;
    private readonly ShareDefinition _b;
    private readonly Mock<IAclService> _acl = new();
    private readonly UserContext _user = new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);
    private readonly BrowseApiController _sut;

    public BrowseApiTransferTests()
    {
        _a = NewShare("a");
        _b = NewShare("b");
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        _acl.Setup(a => a.HasAccessBatchAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<(string, bool)>>(), It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string Path, bool _)> items, FilePermission _) =>
                items.ToDictionary(i => i.Path, _ => true));

        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_user.User.Id)).ReturnsAsync(_user);

        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_a.Id)).ReturnsAsync(_a);
        shares.Setup(r => r.GetByIdAsync(_b.Id)).ReturnsAsync(_b);

        var factory = new Mock<IFileServiceFactory>();
        factory.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>()))
            .Returns<ShareDefinition>(s => new FileService(
                new FileSystemStorage(s.Path, s.Id, null), _acl.Object, null, s.Id));

        _sut = new BrowseApiController(users.Object, shares.Object, factory.Object, null!,
            NullLogger<BrowseApiController>.Instance)
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ShareDefinition NewShare(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return new ShareDefinition(name, path);
    }

    private static string P(ShareDefinition s, string rel) => Path.Combine(s.Path, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Put(ShareDefinition s, string rel, string content, DateTime? mtime = null)
    {
        var full = P(s, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (mtime is { } t) File.SetLastWriteTimeUtc(full, t);
    }

    // Creation time as the server stores and reads it (user.DOSATTRIB on Linux, where .NET's
    // CreationTimeUtc is only min(mtime, ctime) and setting it writes the mtime).
    private static DateTime Created(string full)
        => FileBirthTime.GetUtc(Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full));

    private static void SetCreated(string full, DateTime utc) => FileBirthTime.TrySetUtc(new FileInfo(full), utc);

    private static TransferRequest Req(ShareDefinition src, string from, ShareDefinition dst, string to, bool overwrite = false)
        => new(src.Id, from, dst.Id, to, overwrite);

    private static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException(r.GetType().Name),
    };

    private static string Code(IActionResult r) => Assert.IsType<ApiError>(Assert.IsAssignableFrom<ObjectResult>(r).Value).Code;

    // ─────────────────── copy ───────────────────

    [Fact]
    public async Task Copy_file_within_share_creates_target_and_keeps_source_and_mtime()
    {
        var mtime = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        Put(_a, "docs/x.txt", "hello", mtime);
        Directory.CreateDirectory(P(_a, "other"));

        var r = await _sut.Copy(Req(_a, "docs/x.txt", _a, "other/x.txt"));

        Assert.Equal(201, Status(r));
        Assert.Equal("hello", File.ReadAllText(P(_a, "other/x.txt")));
        Assert.True(File.Exists(P(_a, "docs/x.txt")));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(P(_a, "other/x.txt")));
    }

    [Fact]
    public async Task Copy_folder_across_shares_is_recursive()
    {
        Put(_a, "f/one.txt", "1");
        Put(_a, "f/sub/two.txt", "22");

        var r = await _sut.Copy(Req(_a, "f", _b, "g"));

        Assert.Equal(201, Status(r));
        Assert.Equal("1", File.ReadAllText(P(_b, "g/one.txt")));
        Assert.Equal("22", File.ReadAllText(P(_b, "g/sub/two.txt")));
        Assert.True(File.Exists(P(_a, "f/sub/two.txt")));
    }

    [Fact]
    public async Task Copy_to_existing_target_without_overwrite_is_409_target_exists()
    {
        Put(_a, "x.txt", "new");
        Put(_b, "x.txt", "old");

        var r = await _sut.Copy(Req(_a, "x.txt", _b, "x.txt"));

        Assert.Equal(409, Status(r));
        Assert.Equal("target_exists", Code(r));
        Assert.Equal("old", File.ReadAllText(P(_b, "x.txt")));
    }

    [Fact]
    public async Task Copy_with_overwrite_replaces_file_and_merges_folder()
    {
        Put(_a, "f/x.txt", "new");
        Put(_b, "f/x.txt", "old");
        Put(_b, "f/keep.txt", "keep");

        var r = await _sut.Copy(Req(_a, "f", _b, "f", overwrite: true));

        Assert.Equal(204, Status(r));
        Assert.Equal("new", File.ReadAllText(P(_b, "f/x.txt")));
        Assert.Equal("keep", File.ReadAllText(P(_b, "f/keep.txt")));
    }

    [Fact]
    public async Task Copy_file_onto_folder_is_409_type_mismatch_even_with_overwrite()
    {
        Put(_a, "x", "file");
        Directory.CreateDirectory(P(_b, "x"));

        var r = await _sut.Copy(Req(_a, "x", _b, "x", overwrite: true));

        Assert.Equal(409, Status(r));
        Assert.Equal("type_mismatch", Code(r));
    }

    [Theory]
    [InlineData("f", "f")]
    [InlineData("f", "f/inner")]
    [InlineData("f", "f/a/b")]
    public async Task Copy_into_itself_is_400_invalid_target(string from, string to)
    {
        Put(_a, "f/x.txt", "1");

        var r = await _sut.Copy(Req(_a, from, _a, to));

        Assert.Equal(400, Status(r));
        Assert.Equal("invalid_target", Code(r));
    }

    [Fact]
    public async Task Copy_missing_source_is_404()
    {
        var r = await _sut.Copy(Req(_a, "nope.txt", _b, "nope.txt"));
        Assert.Equal(404, Status(r));
    }

    [Fact]
    public async Task Copy_with_root_path_is_400()
    {
        var r = await _sut.Copy(Req(_a, "", _b, "x"));
        Assert.Equal(400, Status(r));
    }

    // ─────────────────── move ───────────────────

    [Fact]
    public async Task Move_within_share_relocates_the_item()
    {
        Put(_a, "f/x.txt", "1");
        Directory.CreateDirectory(P(_a, "g"));

        var r = await _sut.Move(Req(_a, "f", _a, "g/f"));

        Assert.Equal(201, Status(r));
        Assert.False(Directory.Exists(P(_a, "f")));
        Assert.Equal("1", File.ReadAllText(P(_a, "g/f/x.txt")));
    }

    [Fact]
    public async Task Move_across_shares_copies_then_deletes_source()
    {
        Put(_a, "f/x.txt", "1");

        var r = await _sut.Move(Req(_a, "f", _b, "f"));

        Assert.Equal(201, Status(r));
        Assert.False(Directory.Exists(P(_a, "f")));
        Assert.Equal("1", File.ReadAllText(P(_b, "f/x.txt")));
    }

    [Fact]
    public async Task Move_with_overwrite_into_existing_folder_merges_and_removes_source()
    {
        Put(_a, "f/x.txt", "new");
        Put(_a, "g/x.txt", "old");
        Put(_a, "g/keep.txt", "keep");

        var r = await _sut.Move(Req(_a, "f", _a, "g", overwrite: true));

        Assert.Equal(204, Status(r));
        Assert.False(Directory.Exists(P(_a, "f")));
        Assert.Equal("new", File.ReadAllText(P(_a, "g/x.txt")));
        Assert.Equal("keep", File.ReadAllText(P(_a, "g/keep.txt")));
    }

    [Fact]
    public async Task Move_to_existing_target_without_overwrite_is_409_and_keeps_source()
    {
        Put(_a, "x.txt", "new");
        Put(_a, "y.txt", "old");

        var r = await _sut.Move(Req(_a, "x.txt", _a, "y.txt"));

        Assert.Equal(409, Status(r));
        Assert.True(File.Exists(P(_a, "x.txt")));
        Assert.Equal("old", File.ReadAllText(P(_a, "y.txt")));
    }

    [Theory]
    [InlineData(false, false)] // across shares: copy + delete
    [InlineData(true, true)]   // within a share into an existing folder: merge copy + delete
    public async Task Move_without_delete_permission_on_source_is_403_and_copies_nothing(bool sameShare, bool overwrite)
    {
        Put(_a, "f/x.txt", "1");
        var target = sameShare ? _a : _b;
        if (overwrite) Directory.CreateDirectory(P(target, "g"));
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), FilePermission.Delete))
            .ReturnsAsync(false);

        var r = await _sut.Move(Req(_a, "f", target, "g", overwrite));

        Assert.Equal(403, Status(r));
        Assert.True(File.Exists(P(_a, "f/x.txt")));
        Assert.False(File.Exists(P(target, "g/x.txt")));
    }

    [Fact]
    public async Task Move_file_with_overwrite_within_share_replaces_the_target()
    {
        Put(_a, "x.txt", "new");
        Put(_a, "y.txt", "old");

        var r = await _sut.Move(Req(_a, "x.txt", _a, "y.txt", overwrite: true));

        Assert.Equal(204, Status(r));
        Assert.Equal("new", File.ReadAllText(P(_a, "y.txt")));
        Assert.False(File.Exists(P(_a, "x.txt")));
    }

    [Fact]
    public async Task Move_file_with_overwrite_that_cannot_write_the_target_keeps_the_old_target()
    {
        // Regression: the old target was deleted before the rename checked write access.
        Put(_a, "x.txt", "new");
        Put(_a, "y.txt", "old");
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), "y.txt",
                It.IsAny<bool>(), FilePermission.CreateWriteData))
            .ReturnsAsync(false);

        var r = await _sut.Move(Req(_a, "x.txt", _a, "y.txt", overwrite: true));

        Assert.Equal(403, Status(r));
        Assert.Equal("old", File.ReadAllText(P(_a, "y.txt")));
        Assert.True(File.Exists(P(_a, "x.txt")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Transfer_into_a_missing_target_folder_is_404_and_creates_nothing(bool isMove, bool sameShare)
    {
        Put(_a, "x.txt", "1");
        var target = sameShare ? _a : _b;
        var req = Req(_a, "x.txt", target, "missing/x.txt");

        var r = isMove ? await _sut.Move(req) : await _sut.Copy(req);

        Assert.Equal(404, Status(r));
        Assert.Equal("target_parent_missing", Code(r));
        Assert.False(Directory.Exists(P(target, "missing")));
        Assert.True(File.Exists(P(_a, "x.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_onto_an_ancestor_of_the_source_is_400_invalid_target(bool isMove)
    {
        // Merging p/c into p would write p/c/c onto the source folder p/c, which a move then deletes.
        Put(_a, "p/c/c/x.txt", "1");
        var req = Req(_a, "p/c", _a, "p", overwrite: true);

        var r = isMove ? await _sut.Move(req) : await _sut.Copy(req);

        Assert.Equal(400, Status(r));
        Assert.Equal("invalid_target", Code(r));
        Assert.Equal("1", File.ReadAllText(P(_a, "p/c/c/x.txt")));
    }

    [Fact]
    public async Task Merge_never_moves_the_folder_time_back_or_its_creation_time_forward()
    {
        var older = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2022, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        Put(_a, "f/x.txt", "new");
        Put(_b, "f/keep.txt", "keep");
        Directory.SetLastWriteTimeUtc(P(_a, "f"), older);
        Directory.SetLastWriteTimeUtc(P(_b, "f"), newer);
        FileBirthTime.TrySetUtc(new DirectoryInfo(P(_a, "f")), newer);
        FileBirthTime.TrySetUtc(new DirectoryInfo(P(_b, "f")), older);

        var r = await _sut.Move(Req(_a, "f", _b, "f", overwrite: true));

        Assert.Equal(204, Status(r));
        Assert.True(Directory.GetLastWriteTimeUtc(P(_b, "f")) >= newer);
        Assert.Equal(older, Created(P(_b, "f")));
    }

    // ─────────────────── error mapping ───────────────────

    /// <summary>Request body that fails with <paramref name="error"/> on the first read.</summary>
    private sealed class FailingStream(Exception error) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw error;
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) => throw error;
    }

    [Theory]
    [InlineData("too_large", 413)]
    [InlineData("disk_full", 507)]
    [InlineData("path_busy", 503)]
    public async Task Upload_failing_for_a_transient_reason_is_not_a_stored_409(string kind, int expected)
    {
        // A 409 is stored for the Idempotency-Key, so a retry would replay it forever.
        Exception error = kind switch
        {
            "too_large" => new BadHttpRequestException("Request body too large.", 413),
            "disk_full" => new IOException("No space left on device", 28), // ENOSPC
            _ => new CloudSyncOperationConflictException("A cloud sync holds the path."),
        };
        _sut.HttpContext.Request.Body = new FailingStream(error);

        var r = await _sut.Upload(_a.Id, "up.txt", default);

        Assert.Equal(expected, Status(r));
    }

    [Fact]
    public async Task Upload_aborted_by_the_client_is_400_not_a_stored_409()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        _sut.HttpContext.RequestAborted = aborted.Token;
        _sut.HttpContext.Request.Body = new FailingStream(new IOException("The client reset the request stream."));

        var r = await _sut.Upload(_a.Id, "up.txt", default);

        Assert.Equal(400, Status(r));
    }

    [Fact]
    public async Task Rename_onto_existing_target_is_409_not_500()
    {
        Put(_a, "x.txt", "1");
        Put(_a, "y.txt", "2");

        var r = await _sut.Rename(_a.Id, new RenameRequest("x.txt", "y.txt"));

        Assert.Equal(409, Status(r));
    }

    [Fact]
    public async Task Move_across_shares_keeps_the_creation_time_but_copy_gets_a_new_one()
    {
        var created = new DateTime(2004, 4, 4, 4, 4, 4, DateTimeKind.Utc);
        Put(_a, "m.txt", "1");
        Put(_a, "c.txt", "2");
        SetCreated(P(_a, "m.txt"), created);
        SetCreated(P(_a, "c.txt"), created);

        Assert.Equal(201, Status(await _sut.Move(Req(_a, "m.txt", _b, "m.txt"))));
        Assert.Equal(201, Status(await _sut.Copy(Req(_a, "c.txt", _b, "c.txt"))));

        Assert.Equal(created, Created(P(_b, "m.txt")));
        Assert.NotEqual(created, Created(P(_b, "c.txt")));
    }

    // ─────────────────── creation time headers ───────────────────

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Upload_with_created_header_stamps_the_creation_time()
    {
        var created = new DateTime(2008, 8, 8, 8, 8, 8, DateTimeKind.Utc);
        _sut.HttpContext.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });
        _sut.HttpContext.Request.Headers[BrowseApiController.CreatedAtHeader] = Ms(created).ToString();

        var r = await _sut.Upload(_a.Id, "up.txt", default);

        Assert.Equal(200, Status(r));
        Assert.Equal(created, Created(P(_a, "up.txt")));
        Assert.Equal(created, Assert.IsType<FileEntryDto>(((ObjectResult)r).Value).CreatedAtUtc);
    }

    [Fact]
    public async Task Upload_with_invalid_created_header_is_400()
    {
        _sut.HttpContext.Request.Body = new MemoryStream(new byte[] { 1 });
        _sut.HttpContext.Request.Headers[BrowseApiController.CreatedAtHeader] = "yesterday";

        var r = await _sut.Upload(_a.Id, "up.txt", default);

        Assert.Equal(400, Status(r));
        Assert.Equal("invalid_created_at", Code(r));
    }

    [Fact]
    public async Task SetModifiedAt_accepts_a_creation_time_alone()
    {
        var created = new DateTime(2009, 9, 9, 9, 9, 9, DateTimeKind.Utc);
        var mtime = new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Put(_a, "f/x.txt", "1");
        Directory.SetLastWriteTimeUtc(P(_a, "f"), mtime);
        _sut.HttpContext.Request.Headers[BrowseApiController.CreatedAtHeader] = Ms(created).ToString();

        Assert.Equal(200, Status(await _sut.SetModifiedAt(_a.Id, "f")));

        Assert.Equal(created, Created(P(_a, "f")));
        Assert.Equal(mtime, Directory.GetLastWriteTimeUtc(P(_a, "f")));
    }

    [Fact]
    public async Task SetModifiedAt_without_any_time_header_is_400()
    {
        Put(_a, "x.txt", "1");
        var r = await _sut.SetModifiedAt(_a.Id, "x.txt");
        Assert.Equal(400, Status(r));
        Assert.Equal("missing_time_header", Code(r));
    }

    [Fact]
    public async Task Download_reports_the_creation_time_header()
    {
        var created = new DateTime(2007, 7, 7, 7, 7, 7, DateTimeKind.Utc);
        Put(_a, "d.txt", "1");
        SetCreated(P(_a, "d.txt"), created);

        var r = await _sut.Download(_a.Id, "d.txt");

        Assert.IsType<FileStreamResult>(r);
        ((FileStreamResult)r).FileStream.Dispose();
        Assert.Equal(Ms(created).ToString(), _sut.Response.Headers[BrowseApiController.CreatedAtHeader].ToString());
    }

    // ─────────────────── size ───────────────────

    [Fact]
    public async Task Size_of_folder_is_recursive_sum()
    {
        Put(_a, "f/one.txt", "1");
        Put(_a, "f/sub/two.txt", "22");

        var r = Assert.IsType<OkObjectResult>(await _sut.Size(_a.Id, "f"));

        var dto = Assert.IsType<SizeDto>(r.Value);
        Assert.True(dto.IsDirectory);
        Assert.Equal(3, dto.Size);
    }

    [Fact]
    public async Task Size_of_file_is_its_length()
    {
        Put(_a, "x.txt", "12345");

        var dto = Assert.IsType<SizeDto>(Assert.IsType<OkObjectResult>(await _sut.Size(_a.Id, "x.txt")).Value);

        Assert.False(dto.IsDirectory);
        Assert.Equal(5, dto.Size);
    }

    [Fact]
    public async Task Size_of_missing_item_is_404()
    {
        Assert.Equal(404, Status(await _sut.Size(_a.Id, "missing")));
    }

    // ─────────────────── missing items ───────────────────

    [Fact]
    public async Task Metadata_of_missing_item_is_404()
    {
        Assert.Equal(404, Status(await _sut.Metadata(_a.Id, "missing.txt")));
        Assert.Equal(404, Status(await _sut.Metadata(_a.Id, "nodir/missing.txt")));
    }

    [Fact]
    public async Task Upload_create_only_of_new_file_is_200()
    {
        _sut.HttpContext.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });
        _sut.HttpContext.Request.Headers.IfNoneMatch = "*";

        var r = await _sut.Upload(_a.Id, "new.txt", default);

        Assert.Equal(200, Status(r));
        Assert.Equal(3, new FileInfo(P(_a, "new.txt")).Length);
    }

    [Fact]
    public async Task Upload_create_only_onto_existing_file_is_412_and_keeps_it()
    {
        Put(_a, "old.txt", "keep");
        _sut.HttpContext.Request.Body = new MemoryStream(new byte[] { 1 });
        _sut.HttpContext.Request.Headers.IfNoneMatch = "*";

        Assert.Equal(412, Status(await _sut.Upload(_a.Id, "old.txt", default)));
        Assert.Equal("keep", File.ReadAllText(P(_a, "old.txt")));
    }

    [Fact]
    public async Task Upload_with_if_match_onto_missing_file_is_412()
    {
        _sut.HttpContext.Request.Body = new MemoryStream(new byte[] { 1 });
        _sut.HttpContext.Request.Headers.IfMatch = "\"0:0\"";

        Assert.Equal(412, Status(await _sut.Upload(_a.Id, "ghost.txt", default)));
        Assert.False(File.Exists(P(_a, "ghost.txt")));
    }

    [Fact]
    public async Task Size_without_list_permission_is_403()
    {
        Put(_a, "f/x.txt", "1");
        _acl.Setup(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<bool>(), FilePermission.ListReadData))
            .ReturnsAsync(false);

        Assert.Equal(403, Status(await _sut.Size(_a.Id, "f")));
    }

    // ─────────────────── Windows-invalid target names ───────────────────

    private string? FinalPathHeader()
        => _sut.HttpContext.Response.Headers[BrowseApiController.FinalPathHeader].FirstOrDefault();

    [Fact]
    public async Task Upload_invalid_name_is_stored_renamed_and_reported()
    {
        _sut.HttpContext.Request.Body = new MemoryStream([1, 2, 3]);

        var result = await _sut.Upload(_a.Id, "new:dir/x?.txt", CancellationToken.None);

        var dto = Assert.IsType<FileEntryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("new_dir/x_.txt", dto.Path);
        Assert.True(File.Exists(P(_a, "new_dir/x_.txt")));
        Assert.Equal("new_dir/x_.txt", FinalPathHeader());
    }

    [Fact]
    public async Task Upload_valid_name_sets_no_final_path_header()
    {
        _sut.HttpContext.Request.Body = new MemoryStream([1]);

        await _sut.Upload(_a.Id, "plain.txt", CancellationToken.None);

        Assert.Null(FinalPathHeader());
    }

    [Fact]
    public async Task CreateDirectory_invalid_name_is_renamed()
    {
        var result = await _sut.CreateDirectory(_a.Id, "CON");

        Assert.Equal(204, Status(result));
        Assert.True(Directory.Exists(P(_a, "CON_")));
        Assert.Equal("CON_", FinalPathHeader());
    }

    [Fact]
    public async Task Rename_to_invalid_name_renames_target()
    {
        Put(_a, "f.txt", "x");

        var result = await _sut.Rename(_a.Id, new RenameRequest("f.txt", "a|b.txt"));

        Assert.Equal(204, Status(result));
        Assert.True(File.Exists(P(_a, "a_b.txt")));
        Assert.Equal("a_b.txt", FinalPathHeader());
    }

    [Fact]
    public async Task Copy_to_invalid_name_renames_target()
    {
        Put(_a, "f.txt", "x");

        var result = await _sut.Copy(Req(_a, "f.txt", _b, "f*.txt"));

        Assert.Equal(201, Status(result));
        Assert.True(File.Exists(P(_b, "f_.txt")));
        Assert.Equal("f_.txt", FinalPathHeader());
    }
}
