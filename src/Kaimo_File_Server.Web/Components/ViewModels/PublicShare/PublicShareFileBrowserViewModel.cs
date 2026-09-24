using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// A file-browser view model for anonymous public share links. It reuses the entire local
/// browser (listing, sort, preview, navigation, multi-select) but:
///   * runs every file operation under the <b>link creator's</b> identity (not a circuit user);
///   * is <b>confined</b> to the shared folder — navigation, "..", and the breadcrumb can never
///     climb above <see cref="ShareLink.RootRelativePath"/>;
///   * is <b>read-only</b> (only opening/preview and directory sizes are enabled);
///   * routes downloads through the policy-checked, rate-limited public endpoint.
/// </summary>
public sealed class PublicShareFileBrowserViewModel : FileBrowserViewModel
{
    private readonly ShareLinkService _shareLinks;
    private readonly IUserContextFactory _userContexts;

    private ShareLink? _link;
    private string _rootPath = "";
    private UserContext? _creatorContext;

    public PublicShareFileBrowserViewModel(
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
        ShareLinkService shareLinks)
        : base(fileServiceFactory, shareRepo, dbFactory, userContextFactory, mgmtAuth, authState,
               logger, searchService, userRepo, downloadTickets, zipTickets, demo, syncRepo, shareLinkRepo)
    {
        _shareLinks = shareLinks;
        _userContexts = userContextFactory;
    }

    /// <summary>Binds the view model to a resolved share link. Call before the first load.</summary>
    public void Initialize(ShareLink link)
    {
        _link = link;
        _rootPath = ShareRelativePath.Normalize(link.RootRelativePath);
    }

    // Anonymous, read-only: only opening/preview and directory sizes; no ACL/version/mutation UI.
    public override BrowserCapabilities Capabilities => new()
    {
        CanOpen = true,
        HasProperties = false,
        ShowDirectorySizes = true,
    };

    protected override string RootPath => _rootPath;

    // URL sub-paths for a public link are relative to the shared root, so the real folder
    // name is never part of the address. Map both directions around that root.
    protected override string ResolveSubPath(string subPath)
        => _rootPath.Length == 0 ? subPath
           : string.IsNullOrEmpty(subPath) ? _rootPath
           : $"{_rootPath}/{subPath}";

    public override string RouteSubPathOf(string shareRelativePath)
    {
        if (_rootPath.Length == 0) return shareRelativePath;
        if (string.Equals(shareRelativePath, _rootPath, StringComparison.OrdinalIgnoreCase))
            return "";
        return shareRelativePath.StartsWith(_rootPath + "/", StringComparison.OrdinalIgnoreCase)
            ? shareRelativePath[(_rootPath.Length + 1)..]
            : shareRelativePath;
    }

    // A public link never offers a "share" action of its own.
    protected override bool AllowShareLinkManagement => false;

    // Every operation runs as the link creator, resolved once and cached for the circuit.
    protected override async Task<UserContext?> GetCurrentUserContextAsync()
    {
        if (_link is null) return null;
        if (_creatorContext is not null) return _creatorContext;
        var creator = await _userContexts.CreateByUserIdAsync(_link.CreatedByUserId);
        // A disabled creator must not keep serving files through their links.
        return _creatorContext = creator is { User.IsEnabled: true } ? creator : null;
    }

    public override async Task<string?> GetDownloadUrlAsync(FileMetadata file)
    {
        if (_link is null || file.IsDirectory) return null;
        if (!ShareRelativePath.TryNormalizeStrict(ShareRelativeOf(file), out var rel, allowRoot: false))
            return null;
        if (!IsWithinLinkRoot(rel)) return null;

        var ticket = _shareLinks.IssueTicket(_link, new[] { rel }, zip: false, downloadName: file.Name);
        return "/api/public/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    public override Task<string?> GetSelectionDownloadUrlAsync(IReadOnlyList<FileMetadata> items)
    {
        if (_link is null || items.Count == 0) return Task.FromResult<string?>(null);

        // A single plain file streams directly.
        if (items.Count == 1 && !items[0].IsDirectory)
            return GetDownloadUrlAsync(items[0]);

        var relatives = new List<string>();
        foreach (var item in items)
            if (ShareRelativePath.TryNormalizeStrict(ShareRelativeOf(item), out var rel, allowRoot: false)
                && IsWithinLinkRoot(rel))
                relatives.Add(rel);
        if (relatives.Count == 0) return Task.FromResult<string?>(null);

        var name = (items.Count == 1 ? items[0].Name : _link.DisplayName) + ".zip";
        var ticket = _shareLinks.IssueTicket(_link, relatives, zip: true, downloadName: name);
        return Task.FromResult<string?>("/api/public/download?ticket=" + Uri.EscapeDataString(ticket));
    }

    private bool IsWithinLinkRoot(string relativePath)
        => _rootPath.Length == 0
           || string.Equals(relativePath, _rootPath, StringComparison.OrdinalIgnoreCase)
           || relativePath.StartsWith(_rootPath + "/", StringComparison.OrdinalIgnoreCase);
}
