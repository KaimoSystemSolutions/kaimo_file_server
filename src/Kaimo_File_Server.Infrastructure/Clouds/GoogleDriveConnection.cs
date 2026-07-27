using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class GoogleDriveConnection : ICloudConnection
{
    private readonly DriveService _service;
    private readonly string _refreshToken;

    public DriveService Service => _service;

    public GoogleDriveConnection(Guid shareId, Dictionary<string, string> data, IConfiguration configuration)
    {
        if (!data.TryGetValue("refreshToken", out var refreshToken))
            throw new InvalidOperationException("Google Drive refresh token is missing");

        if (!data.TryGetValue("scope", out var scopeString))
            throw new InvalidOperationException("Google Drive scope is missing");

        _refreshToken = refreshToken;

        var scopes = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var credential = new UserCredential(
            new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = configuration["GoogleOAuth:ClientId"]!,
                        ClientSecret = configuration["GoogleOAuth:ClientSecret"]!
                    },
                    Scopes = scopes
                }),
            // Token store key now identifies share + this specific credential,
            // since a share can hold several independent Google connections
            // is no longer possible for the *same* provider on one share
            // (see note below), but keeping shareId here is still correct
            // and avoids collisions across shares.
            "share-" + shareId,
            new TokenResponse
            {
                RefreshToken = refreshToken
            });

        _service = new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Kaimo_File_Server"
            });
    }

    public static async Task RevokeTokenAsync(string refreshToken)
    {
        using var http = new HttpClient();

        var response = await http.PostAsync(
            "https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken
            }));

        response.EnsureSuccessStatusCode();
    }

    public async Task Dispose()
    {
        await RevokeTokenAsync(_refreshToken);
        Service.Dispose();
    }

    public string getServiceName() => "Google";
    
    public async Task<string> GetAccountEmailAsync()
    {
        var request = _service.About.Get();
        request.Fields = "user(emailAddress)";

        var about = await request.ExecuteAsync();

        if (string.IsNullOrEmpty(about.User?.EmailAddress))
            throw new InvalidOperationException("Could not retrieve Google account email.");

        return about.User.EmailAddress;
    }

    public async Task<string> GetProfilePictureUrlAsync()
    {
        var request = _service.About.Get();
        request.Fields = "user(photoLink)";

        var about = await request.ExecuteAsync();

        if (string.IsNullOrEmpty(about.User?.PhotoLink))
            throw new InvalidOperationException("Could not retrieve Google account profile picture.");

        return about.User.PhotoLink;
    }
    
    public Task UploadAsync(string path, Stream data) => throw new NotImplementedException();
    public Task DownloadAsync(string path, Stream target) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> ListAsync(string path) => throw new NotImplementedException();
}