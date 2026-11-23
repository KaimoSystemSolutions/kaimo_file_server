using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Files;

/// <summary>
/// Pins who sees the client-device sync emblem in the file browser: the device owner sees only
/// their own devices, a <see cref="ManagementPermission.ManageClientDevices"/> admin sees every
/// device (with its owner), nobody else sees any. Disabled selections and revoked devices never show.
/// </summary>
public class FileBrowserViewModelClientSyncMarkerTests : DatabaseTestBase
{
    private readonly User _alice;
    private readonly User _bob;
    private readonly ShareDefinition _share;
    private readonly ShareDefinition _other;

    public FileBrowserViewModelClientSyncMarkerTests()
    {
        _alice = SeedUser("alice", "Alice");
        _bob = SeedUser("bob", "Bob");
        _share = SeedShare("docs");
        _other = SeedShare("other");
    }

    private FileMetadata Entry(string relative) => new()
    {
        Path = $"{_share.Path}/{relative}",
        Name = relative.Split('/')[^1],
        IsDirectory = true,
    };

    private SyncDevice SeedDevice(User owner, string name, bool revoked = false)
    {
        var device = new SyncDevice
        {
            UserId = owner.Id,
            DisplayName = name,
            RevokedAtUtc = revoked ? DateTime.UtcNow : null,
        };
        using var db = NewContext();
        db.SyncDevices.Add(device);
        db.SaveChanges();
        return device;
    }

    private void SeedProfile(SyncDevice device, string path, SyncMode mode = SyncMode.TwoWay,
        bool enabled = true, Guid? shareId = null)
    {
        using var db = NewContext();
        db.DeviceSyncProfiles.Add(new DeviceSyncProfile
        {
            DeviceId = device.Id,
            UserId = device.UserId,
            ShareId = shareId ?? _share.Id,
            RelativePath = path,
            Mode = mode,
            Enabled = enabled,
        });
        db.SaveChanges();
    }

    private async Task<FileBrowserViewModel> LoadAs(User viewer, bool deviceAdmin = false)
    {
        var mgmtAuth = new Mock<IManagementAuthService>();
        mgmtAuth
            .Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageClientDevices))
            .ReturnsAsync(deviceAdmin);

        var sut = Create(viewer, mgmtAuth);
        await sut.LoadShareAsync(_share.Name);
        Assert.Null(sut.ErrorMessage);
        return sut;
    }

    private FileBrowserViewModel Create(User viewer, Mock<IManagementAuthService> mgmtAuth)
    {
        var fileService = new Mock<IFileService>();
        fileService
            .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());
        var fileServices = new Mock<IFileServiceFactory>();
        fileServices.Setup(f => f.CreateForShare(It.IsAny<ShareDefinition>())).Returns(fileService.Object);

        var shareLinks = new Mock<IShareLinkRepository>();
        shareLinks
            .Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ShareLink>());
        var syncRepo = new Mock<ISyncDefinitionRepository>();
        syncRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        return new FileBrowserViewModel(
            fileServices.Object, ShareRepo(), DbFactory,
            UserContextFactoryFor((viewer.Username, ContextFor(viewer))).Object, mgmtAuth.Object,
            AuthStateFor(viewer.Username), NullLogger<FileBrowserViewModel>.Instance,
            new Mock<ISearchService>().Object, UserRepo(), new FileDownloadTicketStore(),
            new ZipDownloadTicketStore(), new DemoModeOptions(), syncRepo.Object, shareLinks.Object);
    }

    [Fact]
    public async Task Owner_SeesOwnDevice_OnTheSyncedFolder()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects", SyncMode.Pull);

        var sut = await LoadAs(_alice);

        var marker = sut.GetClientSyncMarker(Entry("projects"));
        Assert.NotNull(marker);
        Assert.Equal(SyncMode.Pull, marker.Mode);
        var device = Assert.Single(marker.Devices);
        Assert.Equal("Laptop", device.DeviceName);
        Assert.Null(device.OwnerName); // the viewer's own device
    }

    [Fact]
    public async Task Owner_DoesNotSeeOtherUsersDevices()
    {
        SeedProfile(SeedDevice(_bob, "Bob PC"), "projects");

        var sut = await LoadAs(_alice);

        Assert.Null(sut.GetClientSyncMarker(Entry("projects")));
    }

    [Fact]
    public async Task DeviceAdmin_SeesEveryDevice_WithOwnerNamesForOthers()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");
        SeedProfile(SeedDevice(_bob, "Bob PC"), "projects");

        var sut = await LoadAs(_alice, deviceAdmin: true);

        var marker = sut.GetClientSyncMarker(Entry("projects"))!;
        Assert.Equal(2, marker.Devices.Count);
        Assert.Equal(("Laptop", (string?)null), (marker.Devices[0].DeviceName, marker.Devices[0].OwnerName));
        Assert.Equal(("Bob PC", "Bob"), (marker.Devices[1].DeviceName, marker.Devices[1].OwnerName));
    }

    [Fact]
    public async Task DisabledSelectionAndRevokedDevice_AreHidden()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects", enabled: false);
        SeedProfile(SeedDevice(_alice, "Old phone", revoked: true), "projects");

        var sut = await LoadAs(_alice, deviceAdmin: true);

        Assert.Null(sut.GetClientSyncMarker(Entry("projects")));
    }

    [Fact]
    public async Task DevicesWithDifferentDirections_ShowTwoWay()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects", SyncMode.Pull);
        SeedProfile(SeedDevice(_alice, "Phone"), "projects", SyncMode.Push);

        var sut = await LoadAs(_alice);

        Assert.Equal(SyncMode.TwoWay, sut.GetClientSyncMarker(Entry("projects"))!.Mode);
    }

    [Fact]
    public async Task EntriesBeneathTheSyncedFolder_AreMarked_SiblingsAndOtherSharesAreNot()
    {
        var laptop = SeedDevice(_alice, "Laptop");
        SeedProfile(laptop, "projects", SyncMode.Push);
        SeedProfile(laptop, "archive", shareId: _other.Id);

        var sut = await LoadAs(_alice);

        Assert.Equal(SyncMode.Push, sut.GetClientSyncMarker(Entry("projects/2026/plan.docx"))!.Mode);
        Assert.Null(sut.GetClientSyncMarker(Entry("projects-old")));
        Assert.Null(sut.GetClientSyncMarker(Entry("archive")));
    }

    [Fact]
    public async Task WholeShareSelection_MarksTopLevelEntries()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "");

        var sut = await LoadAs(_alice);

        Assert.NotNull(sut.GetClientSyncMarker(Entry("anything")));
    }

    [Fact]
    public async Task NestedSelectionsOfDifferentDevices_AllApply()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");
        SeedProfile(SeedDevice(_alice, "Phone"), "projects/photos", SyncMode.Pull);

        var sut = await LoadAs(_alice);

        var names = sut.GetClientSyncMarker(Entry("projects/photos/a.jpg"))!.Devices.Select(d => d.DeviceName);
        Assert.Equal(["Laptop", "Phone"], names.Order());
        Assert.Single(sut.GetClientSyncMarker(Entry("projects/doc.txt"))!.Devices);
    }

    [Fact]
    public async Task PathsDifferingOnlyInCase_AreNotMarked()
    {
        // Shares live on case-sensitive filesystems: "Projects" is a different folder.
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");

        var sut = await LoadAs(_alice);

        Assert.Null(sut.GetClientSyncMarker(Entry("Projects")));
        Assert.Null(sut.GetClientSyncMarker(Entry("PROJECTS/a.txt")));
    }

    [Fact]
    public async Task TwoDevicesWithTheSameName_AreBothListed()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");

        var sut = await LoadAs(_alice);

        Assert.Equal(2, sut.GetClientSyncMarker(Entry("projects"))!.Devices.Count);
    }

    [Fact]
    public async Task NestedSelectionsOfOneDevice_ListItOnce_WithTheInnermostMode()
    {
        var laptop = SeedDevice(_alice, "Laptop");
        SeedProfile(laptop, "projects", SyncMode.TwoWay);
        SeedProfile(laptop, "projects/photos", SyncMode.Pull);

        var sut = await LoadAs(_alice);

        var marker = sut.GetClientSyncMarker(Entry("projects/photos/a.jpg"))!;
        var device = Assert.Single(marker.Devices);
        Assert.Equal(SyncMode.Pull, device.Mode);
        Assert.Equal(SyncMode.Pull, marker.Mode);
        Assert.Equal(SyncMode.TwoWay, sut.GetClientSyncMarker(Entry("projects/doc.txt"))!.Mode);
    }

    [Fact]
    public async Task OverlappingLoadOfAnotherShare_DoesNotApplyTheStaleSelections()
    {
        SeedProfile(SeedDevice(_alice, "Laptop"), "projects");

        // Hold the first load (share "docs") inside its permission check until the
        // second load (share "other") has finished.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var mgmtAuth = new Mock<IManagementAuthService>();
        mgmtAuth
            .Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageClientDevices))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                    return Task.FromResult(false);
                reached.SetResult();
                return gate.Task;
            });
        var sut = Create(_alice, mgmtAuth);

        var first = sut.LoadShareAsync(_share.Name);
        await reached.Task;
        await sut.LoadShareAsync(_other.Name);
        gate.SetResult(false);
        await first;

        // "other" has its own "projects" folder; the stale "docs" selection must not mark it.
        Assert.Null(sut.GetClientSyncMarker(new FileMetadata
        {
            Path = $"{_other.Path}/projects",
            Name = "projects",
            IsDirectory = true,
        }));
    }
}
