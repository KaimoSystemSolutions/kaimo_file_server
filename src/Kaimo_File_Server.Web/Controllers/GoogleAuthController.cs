using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.AspNetCore.Mvc;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleOAuthController : ControllerBase
{
    private readonly IShareRepository _shareRepository;
    private readonly ICloudAuthorizationTicketStore _authorizationTickets;
    private readonly IUserContextFactory _userContextFactory;
    private readonly IManagementAuthService _managementAuth;
    private readonly GoogleOAuthService _googleOAuth;
    private readonly GoogleIdentityConfiguration _identity;

    public GoogleOAuthController(
        IShareRepository shareRepository,
        ICloudAuthorizationTicketStore authorizationTickets,
        IUserContextFactory userContextFactory,
        IManagementAuthService managementAuth,
        GoogleOAuthService googleOAuth,
        GoogleIdentityConfiguration identity)
    {
        _shareRepository = shareRepository;
        _authorizationTickets = authorizationTickets;
        _userContextFactory = userContextFactory;
        _managementAuth = managementAuth;
        _googleOAuth = googleOAuth;
        _identity = identity;
    }


    [HttpGet("connect")]
    public async Task<IActionResult> Connect(
        Guid shareId,
        string? path,
        string ticket,
        string? scopeProfile = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound(R("Web_GoogleOAuth_ShareNotFound"));
        var actor = await GetActorAsync();
        if (actor is null)
            return Unauthorized();
        if (!await _managementAuth.CanManageShareAsync(
                actor, shareId, ManagementPermission.CreateSyncs))
            return Forbid();
        if (!await _authorizationTickets.IsValidAsync(
                ticket, shareId, normalizedPath, "google", actor.User.Id, share.DepartmentId))
            return BadRequest(R("Web_GoogleOAuth_InvalidTransaction"));

        var settings = share.CloudSettings;

        var conflict = CloudSyncPaths.FindConflict(settings, normalizedPath);
        if (conflict is not null)
        {
            return BadRequest(
                conflict == normalizedPath
                    ? R("Web_GoogleOAuth_FolderAlreadySynced")
                    : string.Format(R("Web_GoogleOAuth_FolderConflict"), conflict));
        }

        if (!_identity.DelegatedOAuthEnabled)
            return StatusCode(503, R("Web_GoogleOAuth_NotConfigured"));

        GoogleDriveScopeProfile selectedProfile;
        try
        {
            selectedProfile = scopeProfile is null
                ? _identity.DefaultScopeProfile
                : GoogleIdentityConfiguration.ParseScopeProfile(scopeProfile);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(R("Web_GoogleOAuth_InvalidScopeProfile"));
        }

        var start = _googleOAuth.BeginAuthorization(ticket, selectedProfile);
        if (!await _authorizationTickets.TryAttachProtectedContextAsync(
                ticket,
                shareId,
                normalizedPath,
                "google",
                actor.User.Id,
                share.DepartmentId,
                start.ProtectedContext,
                cancellationToken))
            return BadRequest(R("Web_GoogleOAuth_InvalidTransaction"));

        return Redirect(start.AuthorizationUri);
    }


    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        string state,
        string? code = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync();
        if (actor is null)
            return Unauthorized();
        var transaction = await _authorizationTickets.TryConsumeCallbackAsync(
            state,
            "google",
            actor.User.Id,
            cancellationToken);
        if (transaction is null)
            return BadRequest(R("Web_GoogleOAuth_InvalidTransaction"));

        var share = await _shareRepository.GetByIdAsync(transaction.ResourceId);
        if (share is null || transaction.DepartmentId != share.DepartmentId)
            return BadRequest(R("Web_GoogleOAuth_InvalidTransaction"));
        if (!await _managementAuth.CanManageShareAsync(
                actor, share.Id, ManagementPermission.CreateSyncs))
            return Forbid();
        if (!string.IsNullOrWhiteSpace(error))
            return RedirectWithSyncError(R("Web_GoogleOAuth_ConsentDenied"));
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(R("Web_GoogleOAuth_InvalidCallback"));

        GoogleAuthorizationGrant grant;
        try
        {
            grant = await _googleOAuth.CompleteAuthorizationAsync(
                $"share-{share.Id:N}",
                code,
                transaction.ProtectedContext,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is TokenResponseException
                or CryptographicException
                or InvalidOperationException)
        {
            return BadRequest(new
            {
                error = "google_oauth_failed",
                message = R("Web_GoogleOAuth_ExchangeFailed")
            });
        }

        var settings = share.CloudSettings;
        // Re-check for conflicts: the share may have changed while the user
        // was over on Google's consent screen (e.g. an ancestor folder got
        // synced by someone else in the meantime).
        var conflict = CloudSyncPaths.FindConflict(settings, transaction.ResourcePath);
        if (conflict is not null)
        {
            return RedirectWithSyncError(string.Format(
                R("Web_GoogleOAuth_CallbackConflict"),
                transaction.ResourcePath,
                conflict));
        }

        // Append/overwrite this folder's entry; leaves every other synced
        // folder on the share untouched.
        settings.Folders[transaction.ResourcePath] = new SyncedFolder(
            "google",
            new Dictionary<string, string>
            {
                ["refreshToken"] = grant.RefreshToken,
                ["scope"] = grant.GrantedScopes,
                ["scopeProfile"] = grant.ScopeProfile.ToString(),
                ["authorizationMode"] = "delegated"
            })
        {
            RequiresRemoteFolderSelection = true
        };

        share.CloudSettings = settings;
        await _shareRepository.UpdateAsync(share);

        // Return to the provider-neutral management page. The query values let
        // it select the newly created entry without knowing anything about Google.
        return Redirect(
            $"/sync?connectedShare={share.Id}" +
            $"&connectedPath={Uri.EscapeDataString(transaction.ResourcePath)}&remoteFolderRequired=true");
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(Guid shareId, [FromQuery] string? path)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);

        if (await AuthorizeShareSyncAsync(shareId) is { } denied)
            return denied;

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound("Share not found");

        var settings = share.CloudSettings;

        if (!settings.Folders.Remove(normalizedPath))
            return NotFound("This folder is not synced.");

        share.CloudSettings = settings;
        await _shareRepository.UpdateAsync(share);

        return Ok();
    }


    [HttpGet("status")]
    public async Task<IActionResult> Status(Guid shareId, [FromQuery] string? path)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);

        if (await AuthorizeShareSyncAsync(shareId) is { } denied)
            return denied;

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound("Share not found");

        var settings = share.CloudSettings ??new CloudSettings(new Dictionary<string, SyncedFolder>());

        if (settings.Folders.TryGetValue(normalizedPath, out var exact))
            return Ok(new { path = normalizedPath, relation = "exact", provider = exact.Provider });

        var conflict = CloudSyncPaths.FindConflict(settings, normalizedPath);
        if (conflict is not null)
        {
            var relation = CloudSyncPaths.IsSameOrAncestor(conflict, normalizedPath)
                ? "ancestor-synced"   // this folder lives inside an already-synced folder
                : "descendant-synced"; // an already-synced folder lives inside this one

            return Ok(new
            {
                path = normalizedPath,
                relation,
                conflictPath = conflict,
                provider = settings.Folders[conflict].Provider
            });
        }

        return Ok(new { path = normalizedPath, relation = "none" });
    }


    /// <summary>
    /// Same guard as <see cref="Connect"/>: an authenticated actor with the CreateSyncs
    /// permission on the share. Returns null when access is granted.
    /// </summary>
    private async Task<IActionResult?> AuthorizeShareSyncAsync(Guid shareId)
    {
        var actor = await GetActorAsync();
        if (actor is null)
            return Unauthorized();
        if (!await _managementAuth.CanManageShareAsync(actor, shareId, ManagementPermission.CreateSyncs))
            return Forbid();
        return null;
    }

    private async Task<Kaimo_File_Server.Core.Domain.Identity.UserContext?> GetActorAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return null;
        return await _userContextFactory.CreateByUsernameAsync(username);
    }

    private static RedirectResult RedirectWithSyncError(string message)
        => new($"/sync?syncError={Uri.EscapeDataString(message)}");

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
