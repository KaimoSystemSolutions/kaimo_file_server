using Kaimo_File_Server.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class CloudProviderFactory : ICloudProviderFactory
{
    private readonly IConfiguration _configuration;

    public CloudProviderFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }
    
    public ICloudConnection Create(ShareDefinition share)
    {
        if (share.CloudSettings is null)
            throw new InvalidOperationException("No cloud configured");


        var settings =
            CloudSettings.Deserialize(share.CloudSettings);


        return settings.Provider switch
        {
            "google" => new GoogleDriveConnection(
                share,
                _configuration),

            /*
            "dropbox" => new DropboxConnection(
                share,
                _configuration),
            */
            _ => throw new NotSupportedException(
                settings.Provider)
        };
    }
}