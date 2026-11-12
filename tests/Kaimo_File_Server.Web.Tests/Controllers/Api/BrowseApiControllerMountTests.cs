using System.Security.Claims;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Repositories.ClientSync;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.ClientSync;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Web.Controllers.Api;
using Kaimo_File_Server.Web.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Kaimo_File_Server.Core.Services.Search;

namespace Kaimo_File_Server.Web.Tests.Controllers.Api;

/// <summary>
/// The client API over a virtual share mounted at Team/Cloud of a real local share: the mount
/// shows up as a folder, and everything below it reaches the provider through the adapter.
/// </summary>
public sealed class BrowseApiControllerMountTests : IDisposable
{
    private readonly MountTestHarness _h = new();
    private readonly BrowseApiController _sut;

    public BrowseApiControllerMountTests()
    {
        _h.Remote.Directories.Add("root/docs");
        _h.Remote.Files["root/a.txt"] = "hello"u8.ToArray();
        _h.Remote.Files["root/docs/report.txt"] = "12345"u8.ToArray();
        LocalFile("Team/local.txt", "local");

        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_h.Host.Id)).ReturnsAsync(_h.Host);
        _sut = new BrowseApiController(users.Object, shares.Object, _h.Factory.Object, null!,
            Mock.Of<IManagementAuthService>(), _h.NewRouter(), NullLogger<BrowseApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
    }

    public void Dispose() => _h.Dispose();

    private DefaultHttpContext NewContext() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _h.User.User.Id.ToString())], "test"))
    };

    private string Local(string rel) => Path.Combine(_h.HostPath, rel.Replace('/', Path.DirectorySeparatorChar));

    private void LocalFile(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(rel))!);
        File.WriteAllText(Local(rel), content);
    }

    private Guid Id => _h.Host.Id;

    private static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static string? Code(IActionResult r) => ((r as ObjectResult)?.Value as ApiError)?.Code;

    private static List<FileEntryDto> Entries(IActionResult r)
        => Assert.IsType<List<FileEntryDto>>(Assert.IsType<OkObjectResult>(r).Value);

    private void Body(string text) => _sut.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(text));

    private TransferRequest Req(string from, string to, bool overwrite = false) => new(Id, from, Id, to, overwrite);

    // ── listing ──────────────────────────────────────────────────────

    [Fact]
    public async Task List_OfTheParent_ShowsTheMountAsVirtualFolder()
    {
        var entries = Entries(await _sut.List(Id, "Team"));

        var mount = Assert.Single(entries, e => e.Name == "Cloud");
        Assert.True(mount.IsDirectory);
        Assert.Equal("Team/Cloud", mount.Path);
        Assert.True(mount.IsVirtualFolder);
        Assert.False(Assert.Single(entries, e => e.Name == "local.txt").IsVirtualFolder);
    }

    [Fact]
    public async Task List_Etag_CoversTheMountEntry()
    {
        await _sut.List(Id, "Team");
        var withMount = _sut.Response.Headers.ETag.ToString();

        _h.Granted = null;
        var sut = NewControllerWithFreshRouter();
        await sut.List(Id, "Team");

        Assert.NotEqual(withMount, sut.Response.Headers.ETag.ToString());
    }

    private BrowseApiController NewControllerWithFreshRouter()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_h.Host.Id)).ReturnsAsync(_h.Host);
        return new BrowseApiController(users.Object, shares.Object, _h.Factory.Object, null!,
            Mock.Of<IManagementAuthService>(), _h.NewRouter(), NullLogger<BrowseApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
    }

    [Fact]
    public async Task Upload_WithAStaleIfMatch_IsRefused_DespiteACachedListing()
    {
        await _sut.Metadata(Id, "Team/Cloud/a.txt");
        var tag = _sut.Response.Headers.ETag.ToString();
        _h.Remote.ModifiedAt = _h.Remote.ModifiedAt.AddHours(1);

        var sut = NewControllerWithFreshRouter();
        sut.HttpContext.Request.Method = "PUT";
        sut.HttpContext.Request.Headers.IfMatch = tag;
        sut.HttpContext.Request.Body = new MemoryStream("new"u8.ToArray());

        Assert.Equal(412, Status(await sut.Upload(Id, "Team/Cloud/a.txt", CancellationToken.None)));
        Assert.Equal("hello", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
    }

    [Fact]
    public async Task Delete_SeesAFileCreatedOutsideKaimo_DespiteACachedListing()
    {
        await _sut.List(Id, "Team/Cloud");
        _h.Remote.Files["root/external.txt"] = [];

        var sut = NewControllerWithFreshRouter();
        sut.HttpContext.Request.Method = "DELETE";

        Assert.Equal(204, Status(await sut.Delete(Id, "Team/Cloud/external.txt")));
        Assert.False(_h.Remote.Files.ContainsKey("root/external.txt"));
    }

    [Fact]
    public async Task Rename_SeesAFileCreatedOutsideKaimo_DespiteACachedListing()
    {
        await _sut.List(Id, "Team/Cloud");
        _h.Remote.Files["root/external.txt"] = "e"u8.ToArray();

        var sut = NewControllerWithFreshRouter();
        await sut.Rename(Id, new RenameRequest("Team/Cloud/external.txt", "Team/Cloud/renamed.txt"));

        Assert.True(_h.Remote.Files.ContainsKey("root/renamed.txt"));
    }

    [Fact]
    public async Task Metadata_KeepsUsingTheListingCache()
    {
        await _sut.List(Id, "Team/Cloud");
        var listed = _h.Remote.ListCalls["root"];

        var sut = NewControllerWithFreshRouter();
        sut.HttpContext.Request.Method = "GET";
        await sut.Metadata(Id, "Team/Cloud/a.txt");

        Assert.Equal(listed, _h.Remote.ListCalls["root"]);
    }

    [Fact]
    public async Task List_InsideTheMount_ReturnsRemoteEntriesWithHostPaths()
    {
        var entries = Entries(await _sut.List(Id, "Team/Cloud"));
        Assert.Equal(["Team/Cloud/a.txt", "Team/Cloud/docs"], entries.Select(e => e.Path).Order());
        Assert.All(entries, e => Assert.True(e.IsVirtualFolder));
    }

    [Fact]
    public async Task Metadata_OfTheMountRoot_IsAFlaggedFolder()
    {
        var dto = Assert.IsType<FileEntryDto>(Assert.IsType<OkObjectResult>(await _sut.Metadata(Id, "Team/Cloud")).Value);
        Assert.True(dto.IsDirectory);
        Assert.True(dto.IsVirtualFolder);
    }

    // ── without a grant nothing changes ──────────────────────────────

    [Fact]
    public async Task WithoutGrant_TheMountIsInvisible_AndTheNameStaysReserved()
    {
        _h.Granted = null;
        var sut = NewControllerWithFreshRouter();

        Assert.DoesNotContain(Entries(await sut.List(Id, "Team")), e => e.Name == "Cloud");
        Assert.Equal(404, Status(await NewControllerWithFreshRouter().List(Id, "Team/Cloud")));
        Assert.Equal(403, Status(await NewControllerWithFreshRouter().CreateDirectory(Id, "Team/Cloud")));
    }

    // ── content ──────────────────────────────────────────────────────

    [Fact]
    public async Task Download_StreamsTheRemoteFile_WithRangeProcessing()
    {
        var file = Assert.IsType<FileStreamResult>(await _sut.Download(Id, "Team/Cloud/a.txt"));
        Assert.True(file.EnableRangeProcessing);
        using var reader = new StreamReader(file.FileStream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Upload_WritesToTheRemote_AndReturnsAFlaggedEntry()
    {
        Body("data");
        var dto = Assert.IsType<FileEntryDto>(Assert.IsType<OkObjectResult>(
            await _sut.Upload(Id, "Team/Cloud/new.txt", CancellationToken.None)).Value);

        Assert.Equal("data", Encoding.UTF8.GetString(_h.Remote.Files["root/new.txt"]));
        Assert.Equal("Team/Cloud/new.txt", dto.Path);
        Assert.True(dto.IsVirtualFolder);
    }

    [Fact]
    public async Task Upload_InvalidWindowsName_IsRenamedWithoutToAbsolutePath()
    {
        Body("data");
        await _sut.Upload(Id, "Team/Cloud/a:b.txt", CancellationToken.None);

        Assert.True(_h.Remote.Files.ContainsKey("root/a_b.txt"));
        Assert.Equal("Team/Cloud/a_b.txt", _sut.Response.Headers[BrowseApiController.FinalPathHeader].ToString());
    }

    [Fact]
    public async Task Upload_ExistingRemoteNameThatWindowsCannotUse_KeepsItsName()
    {
        _h.Remote.Files["root/ab:c.txt"] = "old"u8.ToArray();
        Body("data");
        await _sut.Upload(Id, "Team/Cloud/ab:c.txt", CancellationToken.None);

        Assert.Equal("data", Encoding.UTF8.GetString(_h.Remote.Files["root/ab:c.txt"]));
        Assert.False(_h.Remote.Files.ContainsKey("root/ab_c.txt"));
    }

    [Fact]
    public async Task ReadOnlyMount_RefusesUpload()
    {
        _h.Granted = CloudAccessPermission.Read;
        var sut = NewControllerWithFreshRouter();
        sut.HttpContext.Request.Body = new MemoryStream("x"u8.ToArray());
        Assert.Equal(403, Status(await sut.Upload(Id, "Team/Cloud/new.txt", CancellationToken.None)));
    }

    // ── mkdir / delete / size ────────────────────────────────────────

    [Fact]
    public async Task CreateDirectory_InTheMount()
    {
        Assert.Equal(204, Status(await _sut.CreateDirectory(Id, "Team/Cloud/sub")));
        Assert.Contains("root/sub", _h.Remote.Directories);
    }

    [Fact]
    public async Task Delete_InTheMount_IsPermanent()
    {
        Assert.Equal(204, Status(await _sut.Delete(Id, "Team/Cloud/a.txt")));
        Assert.False(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Delete_OfTheMountRoot_IsForbidden()
        => Assert.Equal(403, Status(await _sut.Delete(Id, "Team/Cloud")));

    [Fact]
    public async Task Size_WalksTheRemoteFolder()
    {
        var dto = Assert.IsType<SizeDto>(Assert.IsType<OkObjectResult>(await _sut.Size(Id, "Team/Cloud")).Value);
        Assert.Equal(10, dto.Size);
    }

    // ── versions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Versions_AreEmpty()
    {
        var result = Assert.IsType<OkObjectResult>(await _sut.Versions(Id, "Team/Cloud/a.txt"));
        Assert.Empty(Assert.IsType<List<FileVersionDto>>(result.Value));
    }

    [Fact]
    public async Task VersionContent_IsNotFound()
        => Assert.Equal(404, Status(await _sut.VersionContent(Id, "Team/Cloud/a.txt", "2026-01-01T00:00:00Z")));

    // ── rename ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_InsideTheMount()
    {
        Assert.Equal(204, Status(await _sut.Rename(Id, new RenameRequest("Team/Cloud/a.txt", "Team/Cloud/b.txt"))));
        Assert.True(_h.Remote.Files.ContainsKey("root/b.txt"));
    }

    [Fact]
    public async Task Rename_LocalIntoTheMount_CopiesThenDeletes()
    {
        Assert.Equal(204, Status(await _sut.Rename(Id, new RenameRequest("Team/local.txt", "Team/Cloud/moved.txt"))));
        Assert.True(_h.Remote.Files.ContainsKey("root/moved.txt"));
        Assert.False(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Rename_LocalOntoAnExistingRemoteItem_IsAConflict()
    {
        var result = await _sut.Rename(Id, new RenameRequest("Team/local.txt", "Team/Cloud/a.txt"));
        Assert.Equal(409, Status(result));
        Assert.Equal("hello", Encoding.UTF8.GetString(_h.Remote.Files["root/a.txt"]));
        Assert.True(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Rename_RemoteOutOfTheMount_IsRefused()
    {
        var result = await _sut.Rename(Id, new RenameRequest("Team/Cloud/a.txt", "Team/a.txt"));
        Assert.Equal(403, Status(result));
        Assert.Equal("move_not_supported", Code(result));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    // ── copy / move across the boundary ──────────────────────────────

    [Fact]
    public async Task Copy_LocalToRemote()
    {
        Assert.Equal(201, Status(await _sut.Copy(Req("Team/local.txt", "Team/Cloud/copy.txt"))));
        Assert.Equal("local", Encoding.UTF8.GetString(_h.Remote.Files["root/copy.txt"]));
        Assert.True(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Copy_RemoteToLocal()
    {
        Assert.Equal(201, Status(await _sut.Copy(Req("Team/Cloud/a.txt", "Team/copy.txt"))));
        Assert.Equal("hello", File.ReadAllText(Local("Team/copy.txt")));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
    }

    [Fact]
    public async Task Copy_RemoteFolderToLocal_IsRecursive()
    {
        Assert.Equal(201, Status(await _sut.Copy(Req("Team/Cloud/docs", "Team/docs"))));
        Assert.Equal("12345", File.ReadAllText(Local("Team/docs/report.txt")));
    }

    [Fact]
    public async Task Move_LocalToRemote_DeletesTheSourceAfterwards()
    {
        Assert.Equal(201, Status(await _sut.Move(Req("Team/local.txt", "Team/Cloud/moved.txt"))));
        Assert.True(_h.Remote.Files.ContainsKey("root/moved.txt"));
        Assert.False(File.Exists(Local("Team/local.txt")));
    }

    [Fact]
    public async Task Move_RemoteToLocal_IsRefused_AndChangesNothing()
    {
        var result = await _sut.Move(Req("Team/Cloud/a.txt", "Team/a.txt"));
        Assert.Equal(403, Status(result));
        Assert.Equal("move_not_supported", Code(result));
        Assert.True(_h.Remote.Files.ContainsKey("root/a.txt"));
        Assert.False(File.Exists(Local("Team/a.txt")));
    }

    [Fact]
    public async Task Move_InsideTheMount_RenamesAtTheProvider()
    {
        Assert.Equal(201, Status(await _sut.Move(Req("Team/Cloud/a.txt", "Team/Cloud/docs/a.txt"))));
        Assert.True(_h.Remote.Files.ContainsKey("root/docs/a.txt"));
    }

    [Fact]
    public async Task Move_LocalOntoAMissingRemoteFolder_ReportsMissingParent()
    {
        var result = await _sut.Move(Req("Team/local.txt", "Team/Cloud/missing/x.txt"));
        Assert.Equal(404, Status(result));
        Assert.Equal("target_parent_missing", Code(result));
    }

    // ── failures ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectionNotReady_Answers503_NotAStorableConflict()
    {
        _h.Connection.State = StorageConnectionState.Disabled;
        var result = await _sut.List(Id, "Team/Cloud");
        Assert.Equal(503, Status(result));
        Assert.Equal("remote_unavailable", Code(result));
    }

    // ── sync exclusion ───────────────────────────────────────────────

    private (SyncApiController Sut, Mock<IDeviceSyncProfileRepository> Profiles, Guid DeviceId) NewSyncController()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetByIdAsync(_h.Host.Id)).ReturnsAsync(_h.Host);
        var device = new SyncDevice { Id = Guid.NewGuid(), UserId = _h.User.User.Id };
        var devices = new Mock<ISyncDeviceRepository>();
        devices.Setup(d => d.GetByIdAsync(device.Id)).ReturnsAsync(device);
        var profiles = new Mock<IDeviceSyncProfileRepository>();
        profiles.Setup(p => p.CreateAsync(It.IsAny<DeviceSyncProfile>()))
            .ReturnsAsync((DeviceSyncProfile p) => p);
        var sut = new SyncApiController(users.Object, devices.Object, profiles.Object, shares.Object,
            _h.Factory.Object, Mock.Of<ISyncQueryService>(), Mock.Of<IFileChangeCursorRepository>(),
            Mock.Of<IFileChangeLogRepository>(), _h.NewRouter())
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
        return (sut, profiles, device.Id);
    }

    [Theory]
    [InlineData("Team/Cloud")]
    [InlineData("Team/Cloud/docs")]
    public async Task SyncProfile_InAMount_IsRefused(string path)
    {
        var (sut, profiles, deviceId) = NewSyncController();
        var result = await sut.CreateProfile(new CreateSyncProfileRequest(deviceId, Id, path, "C:/x", default, true));
        Assert.Equal(409, Status(result));
        Assert.Equal("sync_root_in_mount", Code(result));
        profiles.Verify(p => p.CreateAsync(It.IsAny<DeviceSyncProfile>()), Times.Never);
    }

    [Fact]
    public async Task SyncProfile_MovedIntoAMount_IsRefused()
    {
        var (sut, profiles, _) = NewSyncController();
        var profile = new DeviceSyncProfile { Id = Guid.NewGuid(), UserId = _h.User.User.Id, ShareId = Id, RelativePath = "Team" };
        profiles.Setup(p => p.GetByIdAsync(profile.Id)).ReturnsAsync(profile);

        var result = await sut.UpdateProfile(profile.Id, new UpdateSyncProfileRequest("Team/Cloud", "C:/x", default, true));

        Assert.Equal("sync_root_in_mount", Code(result));
        Assert.Equal("Team", profile.RelativePath);
    }

    [Fact]
    public async Task SyncProfile_OutsideMounts_StillWorks()
    {
        var (sut, _, deviceId) = NewSyncController();
        var result = await sut.CreateProfile(new CreateSyncProfileRequest(deviceId, Id, "Team", "C:/x", default, true));
        Assert.Equal(201, Status(result));
    }

    [Fact]
    public async Task Sync_ExcludesStandaloneVirtualShares()
    {
        var (sut, profiles, deviceId) = NewSyncController();

        Assert.Equal(404, Status(await sut.Delta(_h.Standalone.Id, null)));
        Assert.Equal(404, Status(await sut.CreateProfile(
            new CreateSyncProfileRequest(deviceId, _h.Standalone.Id, "", "C:/x", default, true))));
        profiles.Verify(p => p.CreateAsync(It.IsAny<DeviceSyncProfile>()), Times.Never);
    }

    // ── search ───────────────────────────────────────────────────────

    private (SearchApiController Sut, Mock<ISearchService> Search) NewSearchController()
    {
        var users = new Mock<IUserContextFactory>();
        users.Setup(f => f.CreateByUserIdAsync(_h.User.User.Id)).ReturnsAsync(_h.User);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_h.Host]);
        var search = new Mock<ISearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), _h.User, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync([]);
        var sut = new SearchApiController(users.Object, shares.Object, search.Object, _h.NewRouter())
        {
            ControllerContext = new ControllerContext { HttpContext = NewContext() }
        };
        return (sut, search);
    }

    private static List<SearchHitDto> Hits(IActionResult r)
        => Assert.IsType<List<SearchHitDto>>(Assert.IsType<OkObjectResult>(r).Value);

    [Fact]
    public async Task Search_InAMount_WalksNames_AndReportsHostPaths()
    {
        var (sut, search) = NewSearchController();

        var hit = Assert.Single(Hits(await sut.Search(new SearchRequestDto("REPORT", Id, "Team/Cloud"))));

        Assert.Equal("Team/Cloud/docs/report.txt", hit.Path);
        Assert.Equal("report.txt", hit.Name);
        Assert.Equal("txt", hit.FileType);
        Assert.Equal(5, hit.Size);
        Assert.Equal(Id, hit.ShareId);
        search.Verify(s => s.SearchAsync(It.IsAny<string>(), _h.User, It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Search_HonorsTheLimit()
    {
        _h.Remote.Files["root/report2.txt"] = [];
        var (sut, _) = NewSearchController();
        Assert.Single(Hits(await sut.Search(new SearchRequestDto("report", Id, "Team/Cloud", Limit: 1))));
    }

    [Fact]
    public async Task Search_OutsideMounts_StaysOnTheIndex()
    {
        var (sut, search) = NewSearchController();
        await sut.Search(new SearchRequestDto("report", Id, "Team"));
        search.Verify(s => s.SearchAsync("report", _h.User, _h.Host.Name, "Team",
            It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Search_InAMountWithoutGrant_StaysOnTheIndex()
    {
        _h.Granted = null;
        var (sut, search) = NewSearchController();
        await sut.Search(new SearchRequestDto("report", Id, "Team/Cloud"));
        search.Verify(s => s.SearchAsync("report", _h.User, _h.Host.Name, "Team/Cloud",
            It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }
}
