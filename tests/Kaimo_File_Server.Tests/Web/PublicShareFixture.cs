using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// One public download link on a share, with mocks that behave like the real repositories:
/// the token lookup always returns the current (mutable) state, and consuming an access applies
/// the same guarded predicate as the atomic database update.
/// </summary>
internal sealed class PublicShareFixture
{
    public const string Token = "public-token";
    public static readonly byte[] Content = "hello public world"u8.ToArray();

    public readonly User Creator = new(Guid.NewGuid(), "Alice", "alice", "hash", "nt");
    public readonly ShareDefinition Share = new("internal-share", "/data/internal-share", isEnabled: true);
    public readonly ShareLink Link;
    public readonly ShareLinkSettings Settings = new() { RestrictToDefaultDomain = false, AllowUploadLinks = true };
    public ServerDomainSettings? Domains;
    public bool Deleted;
    public bool DemoReadOnly;

    public readonly Mock<IShareLinkRepository> Links = new();
    public readonly Mock<IShareRepository> Shares = new();
    public readonly Mock<IUserContextFactory> UserContexts = new();
    public readonly Mock<IConfigRepository> Config = new();
    public readonly Mock<INotificationPublisher> Notifications = new();
    public readonly Mock<IFileService> FileService = new();
    public readonly Mock<IFileServiceFactory> FileServices = new();
    public readonly Mock<IManagementAuthService> MgmtAuth = new();
    public readonly Mock<ISyncDefinitionRepository> SyncRepo = new();
    public readonly LinkAccessNotificationGate Gate = new();
    public readonly PublicDownloadTicketStore Tickets = new();

    public PublicShareFixture()
    {
        Link = new ShareLink
        {
            Token = Token,
            ShareId = Share.Id,
            RootRelativePath = "projects/secret",
            IsDirectory = true,
            DisplayName = "Public folder",
            CreatedByUserId = Creator.Id,
        };

        Links.Setup(r => r.GetByTokenAsync(Token)).ReturnsAsync(() => Deleted ? null : Link);
        Links.Setup(r => r.TryConsumeAccessAsync(Token)).ReturnsAsync(() =>
        {
            if (DemoReadOnly) throw new ReadOnlyDemoException();
            if (Deleted || Link.Kind != ShareLinkKind.Download || !Link.IsCurrentlyActive(DateTime.UtcNow))
                return null;
            Link.AccessCount++;
            return Link;
        });

        Shares.Setup(r => r.GetByIdAsync(Share.Id)).ReturnsAsync(Share);
        Shares.Setup(r => r.GetByNameAsync(Share.Name)).ReturnsAsync(Share);

        UserContexts.Setup(f => f.CreateByUserIdAsync(Creator.Id))
            .ReturnsAsync(() => new UserContext(Creator, [], [], []));

        Config.Setup(c => c.GetAsync(ShareLinkSettings.ConfigKey, It.IsAny<ShareLinkSettings>()))
            .ReturnsAsync(() => Settings);
        Config.Setup(c => c.GetAsync(ServerDomainSettings.ConfigKey, It.IsAny<ServerDomainSettings?>()))
            .ReturnsAsync(() => Domains);

        FileService.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new List<FileMetadata>());
        FileService.Setup(s => s.ReadFileAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(() => new MemoryStream(Content));
        FileService.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<UserContext>()))
            .ReturnsAsync(new FileMetadata { Name = "a.txt", IsDirectory = false });
        FileServices.Setup(f => f.CreateForShare(It.Is<ShareDefinition>(s => s.Id == Share.Id)))
            .Returns(FileService.Object);

        MgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
    }

    /// <summary>Number of "link accessed" notifications published so far.</summary>
    public int Notified => Notifications.Invocations.Count(i => i.Method.Name == nameof(INotificationPublisher.PublishAsync));

    /// <summary>Number of file reads that reached the storage layer.</summary>
    public int Reads => FileService.Invocations.Count(i => i.Method.Name == nameof(IFileService.ReadFileAsync));

    /// <summary>Takes the link out of service in one of the ways an owner or admin can.</summary>
    public void Revoke(string how)
    {
        switch (how)
        {
            case "disabled": Link.IsEnabled = false; break;
            case "expired": Link.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1); break;
            case "deleted": Deleted = true; break;
            case "downloadsOff": Settings.AllowDownloadLinks = false; break;
            case "creatorDisabled": Creator.IsEnabled = false; break;
            case "shareDisabled": Share.IsEnabled = false; break;
            case "exhausted": Link.MaxAccessCount = 1; Link.AccessCount = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    public PublicShareAccessPolicy CreatePolicy()
        => new(Links.Object, Shares.Object, UserContexts.Object, Config.Object, Gate, Notifications.Object);

    public ShareLinkService CreateShareLinkService()
        => new(Links.Object, Config.Object, Mock.Of<IPasswordService>(), Mock.Of<ILoginThrottle>(),
            MgmtAuth.Object, UserContexts.Object, Mock.Of<AuthenticationStateProvider>(), Tickets);

    public PublicShareFileBrowserViewModel CreateViewModel()
        => new(FileServices.Object, Shares.Object, Mock.Of<IDbContextFactory<ApplicationDbContext>>(),
            UserContexts.Object, MgmtAuth.Object, Mock.Of<AuthenticationStateProvider>(),
            NullLogger<FileBrowserViewModel>.Instance, Mock.Of<ISearchService>(), Mock.Of<IUserRepository>(),
            new FileDownloadTicketStore(), new ZipDownloadTicketStore(), new DemoModeOptions(),
            SyncRepo.Object, Links.Object, CreateShareLinkService(), CreatePolicy());
}
