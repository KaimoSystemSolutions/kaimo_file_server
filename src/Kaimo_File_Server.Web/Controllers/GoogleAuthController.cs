using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Components.ViewModels;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleOAuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IShareRepository _shareRepository;
    private readonly ICloudProviderFactory _cloudFactory;

    public GoogleOAuthController(
        IConfiguration configuration,
        IShareRepository shareRepository,
        ICloudProviderFactory cloudFactory)
    {
        _configuration = configuration;
        _shareRepository = shareRepository;
        _cloudFactory = cloudFactory;
    }


    [HttpGet("connect")]
    public IActionResult Connect(Guid shareId)
    {
        var clientId = _configuration["GoogleOAuth:ClientId"]!;

        var redirectUri =
            Url.Action(
                "Callback",
                "GoogleOAuth",
                null,
                Request.Scheme)!;


        var authUrl =
            "https://accounts.google.com/o/oauth2/v2/auth" +
            "?access_type=offline" +
            "&prompt=consent" +
            $"&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            $"&state={shareId}" +
            "&scope=" +
            Uri.EscapeDataString(
                "https://www.googleapis.com/auth/drive");


        return Redirect(authUrl);
    }


    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string code, string state)
    {
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
            var error = await response.Content.ReadAsStringAsync();
            return BadRequest(error);
        }


        var token = await GoogleTokenResponse
            .FromHttpResponse(response.Content);
        
        if (!Guid.TryParse(state, out var shareId))
            return BadRequest("Invalid state");

        ShareDefinition? share = await _shareRepository.GetByIdAsync(shareId);

        if (share is null)
            return Redirect("/");

        var settings = new CloudSettings(
            "google", new()
            {
                ["refreshToken"] = token.RefreshToken,
                ["scope"] = token.Scope,
            });

        share.CloudSettings = settings.Serialize();
        share.CloudConnection = _cloudFactory.Create(share);
        await _shareRepository.UpdateAsync(share);
        
        var encoded = Uri.EscapeDataString(share.Name);
        return Redirect($"/?successfulConnection={encoded}");
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
    
}