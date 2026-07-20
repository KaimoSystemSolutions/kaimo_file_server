using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

public class GoogleDriveService
{
    private const string AccountPath = "/data/logins/google-service-account.json";
    
    private readonly DriveService _service;
    private readonly ILogger<GoogleDriveService> _logger;

    public GoogleDriveService(ILogger<GoogleDriveService> logger)
    {
        _logger = logger;
        _service = Authenticate();
        TestService();
    }
    
    private DriveService Authenticate()
    {
        
        var credential = GoogleCredential
            .FromFile(AccountPath)
            .CreateScoped(DriveService.Scope.Drive);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Kaimo_File_Server"
        });
    }


    private async Task<bool> TestService()
    {
        try
        {
            var request = _service.About.Get();
            request.Fields = "user";

            var about = await request.ExecuteAsync();

            _logger.LogDebug("Auth OK. Service account: {Email}",
                about.User?.EmailAddress);

            return true;
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Auth FAILED: {Message} (status {Status})",
                ex.Error?.Message, ex.HttpStatusCode);
            return false;
        }
    }
}