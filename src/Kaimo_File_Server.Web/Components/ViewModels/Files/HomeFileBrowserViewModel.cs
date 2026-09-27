using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// The current user's personal home folder, shown as "user". It is the regular local browser
/// confined to <c>users/&lt;userId&gt;</c>: URLs, breadcrumb and share name never reveal the
/// underlying share or folder. Access itself is enforced by the ACL entries the
/// <see cref="HomeDirectoryService"/> grants, exactly as on WebDAV/SMB.
/// </summary>
public sealed class HomeFileBrowserViewModel : FileBrowserViewModel
{
    private readonly HomeDirectoryService _homes;
    private readonly IUserRepository _users;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<FileBrowserViewModel> _log;
    private string _rootPath = "";

    public HomeFileBrowserViewModel(
        IFileServiceFactory fileServiceFactory,
        IShareRepository shareRepo,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IUserContextFactory userContextFactory,
        IManagementAuthService mgmtAuth,
        AuthenticationStateProvider authState,
        ILogger<FileBrowserViewModel> logger,
        ISearchService searchService,
        IUserRepository userRepo,
        FileDownloadTicketStore downloadTickets,
        ZipDownloadTicketStore zipTickets,
        DemoModeOptions demo,
        ISyncDefinitionRepository syncRepo,
        IShareLinkRepository shareLinkRepo,
        HomeDirectoryService homes)
        : base(fileServiceFactory, shareRepo, dbFactory, userContextFactory, mgmtAuth, authState,
               logger, searchService, userRepo, downloadTickets, zipTickets, demo, syncRepo, shareLinkRepo)
    {
        _homes = homes;
        _users = userRepo;
        _auth = authState;
        _log = logger;
    }

    /// <summary>Display name of the home folder in the web UI.</summary>
    public static string DisplayName => Resources.ResourceManager.GetString("Web_Home_DisplayName") ?? "user";

    /// <summary>Name of the backing share once <see cref="InitializeAsync"/> succeeded.</summary>
    public string? HomeShareName { get; private set; }

    protected override string RootPath => _rootPath;

    protected override bool IsHomeBrowser => true;

    // Cloud sync and the ACL editor are administrative features that do not belong in a
    // private home; the ACL editor additionally refuses the home share itself.
    public override BrowserCapabilities Capabilities
        => base.Capabilities with { HasCloudSync = false, HasFileAcls = false };

    public override BrowserShareInfo? CurrentBrowserShare
        => base.CurrentBrowserShare is { } share ? share with { Name = DisplayName } : null;

    /// <summary>
    /// Resolves (and lazily provisions) the signed-in user's home. Returns <c>false</c> when
    /// the user has no usable home: feature not configured, home disabled, or not writable
    /// (read-only demo).
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        HomeShareName = null;
        var name = (await _auth.GetAuthenticationStateAsync()).User.Identity?.Name;
        if (string.IsNullOrEmpty(name) || await _users.GetByUsernameAsync(name) is not { } user)
            return false;

        try
        {
            if (await _homes.EnsureHomeAsync(user) is not { } home)
                return false;
            _rootPath = home.Path;
            HomeShareName = home.Share.Name;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Home folder of '{User}' is unavailable", name);
            return false;
        }
    }
}
