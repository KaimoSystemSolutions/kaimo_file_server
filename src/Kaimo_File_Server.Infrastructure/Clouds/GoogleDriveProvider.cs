using System.Net.Http;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>Google Drive registration for the provider-neutral cloud subsystem.</summary>
public sealed class GoogleDriveProvider(
    GoogleIdentityConfiguration identity,
    IHttpClientFactory? httpClientFactory = null) : ICloudProvider
{
    public string Id => "google";
    public string DisplayName => "Google Drive";
    public string AuthorizationEndpoint => "/api/google/connect";

    public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        => new GoogleDriveConnection(shareId, folder.Data, identity, httpClientFactory);
}
