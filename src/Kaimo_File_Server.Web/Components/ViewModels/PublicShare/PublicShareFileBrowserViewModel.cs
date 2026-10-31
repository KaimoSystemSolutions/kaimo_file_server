using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// A file-browser view model for anonymous public share links. It reuses the entire local
/// browser (listing, sort, preview, navigation, multi-select) but:
///   * runs every file operation under the <b>link creator's</b> identity (not a circuit user);
///   * is <b>confined</b> to the shared folder — navigation, "..", and the breadcrumb can never
///     climb above <see cref="ShareLink.RootRelativePath"/>;
///   * is <b>read-only</b> (only opening/preview and directory sizes are enabled);
///   * routes downloads through the policy-checked, rate-limited public endpoint;
///   * re-validates the link on every operation (<see cref="PublicShareAccessPolicy"/>) and
///     counts each preview as one access, like a download.
/// </summary>
public sealed class PublicShareFileBrowserViewModel : FileBrowserViewModel
{
    private readonly ShareLinkService _shareLinks;
    private readonly PublicShareAccessPolicy _accessPolicy;

    private ShareLink? _link;
    private string _rootPath = "";
    private Guid? _visitId;
    private string? _host;

    /// <summary>
    /// Raised when a check finds the link no longer usable (disabled, expired, used up, deleted,
    /// download links switched off, share or creator disabled). The page then shows "unavailable".
    /// </summary>
    public event Action? LinkUnavailable;

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
        ShareLinkService shareLinks,
        PublicShareAccessPolicy accessPolicy)
        : base(fileServiceFactory, shareRepo, dbFactory, userContextFactory, mgmtAuth, authState,
               logger, searchService, userRepo, downloadTickets, zipTickets, demo, syncRepo, shareLinkRepo)
    {
        _shareLinks = shareLinks;
        _accessPolicy = accessPolicy;
    }

    /// <summary>
    /// Binds the view model to a resolved share link. Call before the first load.
    /// <paramref name="visitId"/> identifies the visitor's page session (one "link accessed"
    /// notification per visit); <paramref name="host"/> is the host the page was opened under.
    /// </summary>
    public void Initialize(ShareLink link, Guid? visitId = null, string? host = null)
    {
        _link = link;
        _rootPath = ShareRelativePath.Normalize(link.RootRelativePath);
        _visitId = visitId;
        _host = host;
    }

    // Anonymous, read-only: only opening/preview and directory sizes; no ACL/version/mutation UI,
    // and no hashing (it reads whole files of any size without counting an access).
    public override BrowserCapabilities Capabilities => new()
    {
        CanOpen = true,
        HasProperties = false,
        ShowDirectorySizes = true,
        CanHash = false,
    };

    protected override string RootPath => _rootPath;

    // The internal share name must never reach an external visitor: everything that shows
    // the share (breadcrumb, page title) sees only the link's display name.
    public override BrowserShareInfo? CurrentBrowserShare
        => base.CurrentBrowserShare is { } share && _link is not null
            ? share with { Name = _link.DisplayName }
            : null;

    // A public link never offers a "share" action of its own.
    protected override bool AllowShareLinkManagement => false;

    // Restoring a delete renames out of the recycle bin, which lies outside the shared root.
    protected override bool SupportsUndo => false;

    // Every operation runs as the link creator and re-validates the link fresh from the store
    // (no cache): a revoked, expired or used-up link stops serving on the next folder change.
    protected override async Task<UserContext?> GetCurrentUserContextAsync()
    {
        if (_link is null) return null;
        var access = await _accessPolicy.ValidateAsync(_link.Token, _host);
        if (access.Allowed) return access.Actor;
        LinkUnavailable?.Invoke();
        return null;
    }

    // A preview hands out the content just like a download, so it consumes one access through
    // the same policy. It is never bandwidth-limited: the content is capped at the preview size
    // and buffered server-side anyway.
    public override async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_link is null || _fileService is null || file.IsDirectory || file.Size > GetMaxPreviewSizeBytes())
            return null;
        // Nothing to show: the dialog falls back to the download button without costing an access.
        var kind = FileHelper.GetPreviewKind(file.Name);
        if (kind == PreviewKind.Unsupported) return null;
        // Checked before consuming so a bad path never costs the visitor an access.
        if (!ShareRelativePath.TryNormalizeStrict(ShareRelativeOf(file), out var rel, allowRoot: false)
            || !IsWithinLinkRoot(rel))
            return null;

        var access = await _accessPolicy.ConsumeAsync(_link.Token, _host, [rel]);
        if (!access.Allowed)
        {
            if (access.LinkUnavailable) LinkUnavailable?.Invoke();
            return null;
        }

        await _accessPolicy.NotifyAccessedAsync(access.Link!, _visitId, file.Name);

        await using var stream = await _fileService.ReadFileAsync(access.Paths![0], access.Actor!);
        var data = await ReadAllBytesAsync(stream);
        return (data, FileHelper.GetContentType(file.Name), kind);
    }

    // Not offered here (Capabilities.CanHash); refused anyway so no caller can read through it.
    public override Task<string?> ComputeFileHashAsync(
        FileMetadata file, HashAlgorithmName algorithm, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public override async Task<string?> GetDownloadUrlAsync(FileMetadata file)
    {
        if (_link is null || file.IsDirectory) return null;
        if (!ShareRelativePath.TryNormalizeStrict(ShareRelativeOf(file), out var rel, allowRoot: false))
            return null;
        if (!IsWithinLinkRoot(rel)) return null;
        if (await GetCurrentUserContextAsync() is null) return null;

        var ticket = _shareLinks.IssueTicket(_link, new[] { rel }, zip: false, downloadName: file.Name, _visitId);
        return "/api/public/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    public override async Task<string?> GetSelectionDownloadUrlAsync(IReadOnlyList<FileMetadata> items)
    {
        if (_link is null || items.Count == 0) return null;

        // A single plain file streams directly.
        if (items.Count == 1 && !items[0].IsDirectory)
            return await GetDownloadUrlAsync(items[0]);

        var relatives = new List<string>();
        foreach (var item in items)
            if (ShareRelativePath.TryNormalizeStrict(ShareRelativeOf(item), out var rel, allowRoot: false)
                && IsWithinLinkRoot(rel))
                relatives.Add(rel);
        if (relatives.Count == 0) return null;
        if (await GetCurrentUserContextAsync() is null) return null;

        var name = (items.Count == 1 ? items[0].Name : _link.DisplayName) + ".zip";
        var ticket = _shareLinks.IssueTicket(_link, relatives, zip: true, downloadName: name, _visitId);
        return "/api/public/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    private bool IsWithinLinkRoot(string relativePath)
        => PublicShareAccessPolicy.IsWithin(relativePath, _rootPath);
}
