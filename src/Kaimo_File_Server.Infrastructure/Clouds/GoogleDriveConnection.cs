using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Kaimo_File_Server.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class GoogleDriveConnection : ICloudConnection
{
    private readonly DriveService _service;
    private readonly CloudSettings _settings;
    
    public DriveService Service => _service;


    public GoogleDriveConnection(ShareDefinition share, IConfiguration configuration)
    {
        if (share.CloudSettings is null)
            throw new InvalidOperationException(
                $"Share '{share.Name}' is not set up to use a cloud");
        
        _settings = CloudSettings.Deserialize(share.CloudSettings);
        
        if (!string.Equals(
                "google",
                _settings.Provider,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Share '{share.Name}' is not set up to use google drive. " +
                $"It uses '{_settings.Provider}' instead");
        }


        if (!_settings.Data.TryGetValue("refreshToken", out var refreshToken))
            throw new InvalidOperationException(
                "Google Drive refresh token is missing");


        if (!_settings.Data.TryGetValue("scope", out var scopeString))
            throw new InvalidOperationException(
                "Google Drive scope is missing");


        var scopes = scopeString.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);


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
            "share-" + share.Id,
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
        await RevokeTokenAsync(_settings.Data["refreshToken"]);
        Service.Dispose();
    }

    public string getServiceName()
    {
        return "Google";
    }

    public Task UploadAsync(string path, Stream data)
    {
        throw new NotImplementedException();
    }

    public Task DownloadAsync(string path, Stream target)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<string>> ListAsync(string path)
    {
        throw new NotImplementedException();
    }
}