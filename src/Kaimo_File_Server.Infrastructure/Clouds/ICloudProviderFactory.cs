using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public interface ICloudProviderFactory
{
    public static void initilizeCloud(ICloudProviderFactory factory, ShareDefinition share)
    {
        if(share.CloudSettings is null)
            return;
                
        if(share.CloudConnection is not null)
            return;
                
        share.CloudConnection = factory.Create(share);
    }
    
    ICloudConnection Create(ShareDefinition share);

}