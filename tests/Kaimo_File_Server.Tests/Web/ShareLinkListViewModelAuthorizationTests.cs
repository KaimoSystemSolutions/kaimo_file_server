using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// F20: public links run as their creator, so a link manager on the share gets full access
/// (URL, edit, enable) to a foreign link only when they outrank its creator; otherwise the link
/// may only be disabled or deleted. Every action is re-checked on the server against the stored
/// link. Authorization is NOT mocked: the real <see cref="ManagementAuthService"/> decides.
/// </summary>
public sealed class ShareLinkListViewModelAuthorizationTests : DatabaseTestBase
{
    private readonly ShareDefinition _share;
    private readonly User _manager;
    private readonly User _globalAdmin;
    private readonly User _plain;
    private readonly Guid _managerAssignmentId;

    public ShareLinkListViewModelAuthorizationTests()
    {
        _share = SeedShare("projects");
        _manager = SeedUser("linkmgr");
        _globalAdmin = SeedUser("admin");
        _plain = SeedUser("plain");
        _managerAssignmentId = Assign(_manager.Id,
            SeedRole(ManagementPermission.ManageShareLinks | ManagementPermission.ManageUploadLinks), ScopeType.Share, _share.Id);
        Assign(_globalAdmin.Id, SeedRole(ManagementPermission.FullAdmin), ScopeType.Global, Guid.Empty);
    }

    private Role SeedRole(ManagementPermission perms)
    {
        var role = new Role(Guid.NewGuid(), $"Role-{perms}", perms);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private Guid Assign(Guid principalId, Role role, ScopeType scopeType, Guid scopeId)
    {
        var assignment = new ScopedRoleAssignment(principalId, role.Id, scopeType, scopeId);
        using var db = NewContext();
        db.ScopedRoleAssignments.Add(assignment);
        db.SaveChanges();
        return assignment.Id;
    }

    private ShareLink SeedLink(User creator, bool enabled = true)
        => new ShareLinkRepository(DbFactory).CreateAsync(new ShareLink
        {
            Token = Guid.NewGuid().ToString("N"),
            ShareId = _share.Id,
            RootRelativePath = "docs",
            IsDirectory = true,
            DisplayName = "Link of " + creator.Username,
            CreatedByUserId = creator.Id,
            IsEnabled = enabled,
        }).GetAwaiter().GetResult();

    private ShareLinkService LinkService(User actor) => new(
        new ShareLinkRepository(DbFactory), new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions())),
        new PasswordService(), new LoginThrottle(), BuildManagementAuthService(), new UserContextFactory(DbFactory),
        AuthStateFor(actor.Username), new PublicDownloadTicketStore());

    private ShareLinkListViewModel BuildList(User actor) => new(
        new ShareLinkRepository(DbFactory), ShareRepo(), UserRepo(), BuildManagementAuthService(),
        new UserContextFactory(DbFactory), AuthStateFor(actor.Username), LinkService(actor),
        NullLogger<ShareLinkListViewModel>.Instance);

    private async Task<ShareLink> StoredAsync(Guid id)
    {
        await using var db = NewContext();
        return (await db.ShareLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id))!;
    }

    private static ShareLink Copy(ShareLink link) => new()
    {
        Id = link.Id, Token = link.Token, TokenHash = link.TokenHash, Kind = link.Kind, ShareId = link.ShareId,
        RootRelativePath = link.RootRelativePath, IsDirectory = link.IsDirectory, DisplayName = link.DisplayName,
        CreatedByUserId = link.CreatedByUserId, IsEnabled = link.IsEnabled,
    };

    [Fact]
    public async Task LinkOfAGlobalAdmin_IsRestricted_OnlyDisableAndDeleteWork()
    {
        var foreign = SeedLink(_globalAdmin, enabled: false);
        var sut = BuildList(_manager);
        await sut.LoadAsync();

        var row = sut.Links.Single(r => r.Link.Id == foreign.Id);
        Assert.False(row.CanEdit);
        Assert.Equal("", row.Url);

        var edited = Copy(foreign);
        edited.DisplayName = "Hijacked";
        edited.IsEnabled = true;
        Assert.False(await sut.SaveAsync(edited));
        Assert.False(await sut.SetEnabledAsync(foreign.Id, true));
        Assert.NotNull(sut.ActionError);
        var stored = await StoredAsync(foreign.Id);
        Assert.Equal("Link of admin", stored.DisplayName);
        Assert.False(stored.IsEnabled);

        await using (var db = NewContext())
        {
            await db.ShareLinks.Where(l => l.Id == foreign.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsEnabled, true));
        }
        Assert.True(await sut.SetEnabledAsync(foreign.Id, false));
        Assert.False((await StoredAsync(foreign.Id)).IsEnabled);

        Assert.True(await sut.DeleteAsync(foreign.Id));
        Assert.Null(await StoredAsync(foreign.Id));
    }

    [Fact]
    public async Task OwnLink_And_LinkOfAnOutrankedCreator_HaveFullAccess()
    {
        var own = SeedLink(_manager, enabled: false);
        var outranked = SeedLink(_plain, enabled: false);
        var sut = BuildList(_manager);
        await sut.LoadAsync();

        foreach (var link in new[] { own, outranked })
        {
            var row = sut.Links.Single(r => r.Link.Id == link.Id);
            Assert.True(row.CanEdit);
            Assert.NotEqual("", row.Url);
            Assert.True(await sut.SetEnabledAsync(link.Id, true));
            Assert.True((await StoredAsync(link.Id)).IsEnabled);
            var edited = Copy(link);
            edited.DisplayName = "Renamed";
            Assert.True(await sut.SaveAsync(edited));
            Assert.Equal("Renamed", (await StoredAsync(link.Id)).DisplayName);
        }
    }

    [Fact]
    public async Task ForgedSaveObject_CannotMoveTheLinkOrChangeItsCreatorOrKind()
    {
        var own = SeedLink(_manager);
        var otherShare = SeedShare("sales");
        var sut = BuildList(_manager);
        await sut.LoadAsync();

        var forged = Copy(own);
        forged.DisplayName = "Renamed";
        forged.ShareId = otherShare.Id;
        forged.Kind = ShareLinkKind.Upload;
        forged.CreatedByUserId = _globalAdmin.Id;
        forged.RootRelativePath = "../secret";
        Assert.True(await sut.SaveAsync(forged));

        var stored = await StoredAsync(own.Id);
        Assert.Equal("Renamed", stored.DisplayName);
        Assert.Equal(_share.Id, stored.ShareId);
        Assert.Equal(ShareLinkKind.Download, stored.Kind);
        Assert.Equal(_manager.Id, stored.CreatedByUserId);
        Assert.Equal("docs", stored.RootRelativePath);
    }

    [Fact]
    public async Task RevokedLinkRight_AfterLoad_RefusesSaveAndDelete()
    {
        var own = SeedLink(_manager);
        var sut = BuildList(_manager);
        await sut.LoadAsync();
        await using (var db = NewContext())
        {
            await db.ScopedRoleAssignments.Where(a => a.Id == _managerAssignmentId).ExecuteDeleteAsync();
        }

        var edited = Copy(own);
        edited.DisplayName = "Renamed";
        Assert.False(await sut.SaveAsync(edited));
        Assert.False(await sut.SetEnabledAsync(own.Id, false));
        Assert.False(await sut.DeleteAsync(own.Id));
        var stored = await StoredAsync(own.Id);
        Assert.Equal("Link of linkmgr", stored.DisplayName);
        Assert.True(stored.IsEnabled);
    }

    [Fact]
    public async Task FailedSave_RestoresTheRowFromTheStore()
    {
        // The page edits row.Link in place before saving; a failed write must not leave the
        // unsaved values on screen as if they were stored.
        var own = SeedLink(_manager);
        var real = new ShareLinkRepository(DbFactory);
        var repo = new Mock<IShareLinkRepository>();
        repo.Setup(r => r.ListAllAsync()).Returns(() => real.ListAllAsync());
        repo.Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>())).Returns((IEnumerable<Guid> ids) => real.ListForSharesAsync(ids));
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).Returns((Guid id) => real.GetByIdAsync(id));
        repo.Setup(r => r.UpdateAsync(It.IsAny<ShareLink>())).ThrowsAsync(new TimeoutException("db down"));
        var sut = new ShareLinkListViewModel(repo.Object, ShareRepo(), UserRepo(), BuildManagementAuthService(),
            new UserContextFactory(DbFactory), AuthStateFor(_manager.Username), LinkService(_manager),
            NullLogger<ShareLinkListViewModel>.Instance);
        await sut.LoadAsync();

        var row = sut.Links.Single(r => r.Link.Id == own.Id);
        row.Link.DisplayName = "Unsaved";

        Assert.False(await sut.SaveAsync(row.Link));
        Assert.Equal("Link of linkmgr", sut.Links.Single(r => r.Link.Id == own.Id).Link.DisplayName);
    }

    [Fact]
    public async Task SaveSettings_NeedsGlobalManageSystemSettings()
    {
        var before = await LinkService(_manager).GetSettingsAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            LinkService(_manager).SaveSettingsAsync(new ShareLinkSettings { AllowUploadLinks = !before.AllowUploadLinks }));
        Assert.Equal(before.AllowUploadLinks, (await LinkService(_manager).GetSettingsAsync()).AllowUploadLinks);

        await LinkService(_globalAdmin).SaveSettingsAsync(new ShareLinkSettings { AllowUploadLinks = !before.AllowUploadLinks });
        Assert.Equal(!before.AllowUploadLinks, (await LinkService(_globalAdmin).GetSettingsAsync()).AllowUploadLinks);
    }
}
