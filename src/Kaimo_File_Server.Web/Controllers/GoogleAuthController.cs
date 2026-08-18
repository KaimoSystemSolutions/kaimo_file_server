using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleOAuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IShareRepository _shareRepository;
    private readonly ICloudAuthorizationTicketStore _authorizationTickets;
    private readonly IUserContextFactory _userContextFactory;

    public GoogleOAuthController(
        IConfiguration configuration,
        IShareRepository shareRepository,
        ICloudAuthorizationTicketStore authorizationTickets,
        IUserContextFactory userContextFactory)
    {
        _configuration = configuration;
        _shareRepository = shareRepository;
        _authorizationTickets = authorizationTickets;
        _userContextFactory = userContextFactory;
    }


    [HttpGet("connect")]
    public async Task<IActionResult> Connect(Guid shareId, string? path, string ticket)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound("Share not found");
        var actorId = await GetActorIdAsync();
        if (actorId is null)
            return Unauthorized();
        if (!await _authorizationTickets.IsValidAsync(
                ticket, shareId, normalizedPath, "google", actorId, share.DepartmentId))
            return BadRequest("The cloud authorization request is invalid or has expired.");

        var settings = share.CloudSettings;

        var conflict = CloudSyncPaths.FindConflict(settings, normalizedPath);
        if (conflict is not null)
        {
            return BadRequest(
                conflict == normalizedPath
                    ? "This folder is already synced."
                    : $"This conflicts with the already-synced folder '{conflict}'.");
        }

        var clientId = _configuration["GoogleOAuth:ClientId"]!;

        var redirectUri =
            Url.Action(
                "Callback",
                "GoogleOAuth",
                null,
                Request.Scheme)!;

        var state = CloudSyncPaths.EncodeState(shareId, normalizedPath, ticket);

        var authUrl =
            "https://accounts.google.com/o/oauth2/v2/auth" +
            "?access_type=offline" +
            "&prompt=consent" +
            $"&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            $"&state={Uri.EscapeDataString(state)}" +
            "&scope=" +
            Uri.EscapeDataString(
                "https://www.googleapis.com/auth/drive");

        return Redirect(authUrl);
    }


    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string code, string state)
    {
        var decodedState = CloudSyncPaths.DecodeState(state);
        if (decodedState is null)
            return BadRequest("Invalid state");

        var (shareId, path, ticket) = decodedState.Value;
        var share = await _shareRepository.GetByIdAsync(shareId);
        var actorId = await GetActorIdAsync();
        if (share is null || actorId is null)
            return Unauthorized();
        if (ticket is null
            || !await _authorizationTickets.TryConsumeAsync(
                ticket, shareId, path, "google", actorId, share.DepartmentId))
            return BadRequest("The cloud authorization request is invalid or has expired.");

        var clientId = _configuration["GoogleOAuth:ClientId"]!;
        var clientSecret = _configuration["GoogleOAuth:ClientSecret"]!;

        var redirectUri =
            Url.Action(
                "Callback",
                "GoogleOAuth",
                null,
                Request.Scheme)!;


        using var http = new HttpClient();

        var response = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code"] = code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                }));


        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var providerError = Kaimo_File_Server.Core.Services.ProviderErrorSanitizer.FromResponse(
                response.StatusCode, errorBody);
            return BadRequest(new { error = providerError.Code });
        }


        var token = await GoogleTokenResponse
            .FromHttpResponse(response.Content);

        var settings = share.CloudSettings;
        // Re-check for conflicts: the share may have changed while the user
        // was over on Google's consent screen (e.g. an ancestor folder got
        // synced by someone else in the meantime).
        var conflict = CloudSyncPaths.FindConflict(settings, path);
        if (conflict is not null && conflict != path)
        {
            var msg = Uri.EscapeDataString(
                $"'{path}' now conflicts with the already-synced folder '{conflict}'. " +
                "Nothing was connected.");
            return Redirect($"/sync?syncError={msg}");
        }

        // Append/overwrite this folder's entry; leaves every other synced
        // folder on the share untouched.
        settings.Folders[path] = new SyncedFolder(
            "google",
            new Dictionary<string, string>
            {
                ["refreshToken"] = token.RefreshToken,
                ["scope"] = token.Scope,
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
            $"&connectedPath={Uri.EscapeDataString(path)}&remoteFolderRequired=true");
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(Guid shareId, [FromQuery] string? path)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);

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

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound("Share not found");

        var settings = share.CloudSettings ?? new CloudSettings(new Dictionary<string, SyncedFolder>());

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


    internal sealed class GoogleTokenResponse
    {
        public string AccessToken { get; init; } = null!;
        public string RefreshToken { get; init; } = null!;
        public int ExpiresIn { get; init; }
        public string TokenType { get; init; } = null!;
        public string Scope { get; init; } = null!;

        public static async Task<GoogleTokenResponse> FromHttpResponse(HttpContent content)
        {
            using var stream = await content.ReadAsStreamAsync();

            var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream);

            return new GoogleTokenResponse
            {
                AccessToken = json.GetProperty("access_token").GetString()!,
                RefreshToken = json.GetProperty("refresh_token").GetString()!,
                ExpiresIn = json.GetProperty("expires_in").GetInt32(),
                TokenType = json.GetProperty("token_type").GetString()!,
                Scope = json.GetProperty("scope").GetString()!
            };
        }
    }

    private async Task<Guid?> GetActorIdAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return null;
        return (await _userContextFactory.CreateByUsernameAsync(username))?.User.Id;
    }
}
