using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Dropbox;
using Kaimo_File_Server.Infrastructure.ExternalStorage.GoogleDrive;
using Kaimo_File_Server.Infrastructure.ExternalStorage.OneDrive;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage.Cloud;

/// <summary>Google Drive registration for the provider-neutral cloud subsystem.</summary>
public sealed class GoogleDriveProvider(GoogleIdentityConfiguration identity) : ICloudProvider
{
    public string Id => "google";
    public string DisplayName => "Google Drive";
    public string AuthorizationEndpoint => "/api/google/connect";

    public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        => new GoogleDriveConnection(shareId, folder.Data, identity);
}

/// <summary>Microsoft OneDrive registration for the provider-neutral cloud subsystem.</summary>
public sealed class OneDriveProvider(MicrosoftIdentityConfiguration identity) : ICloudProvider
{
    public string Id => "onedrive";
    public string DisplayName => "Microsoft OneDrive";
    public string AuthorizationEndpoint => "/api/onedrive/connect";

    /// <inheritdoc />
    /// <remarks>
    /// OneDrive uses the refresh token stored on the folder mapping; no secret
    /// or provider-specific object is exposed to UI consumers.
    /// </remarks>
    public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        => new OneDriveConnection(folder.Data, identity: identity);
}

/// <summary>Dropbox registration for the provider-neutral cloud subsystem.</summary>
public sealed class DropboxProvider(DropboxIdentityConfiguration identity) : ICloudProvider
{
    public string Id => "dropbox";
    public string DisplayName => "Dropbox";
    public string AuthorizationEndpoint => "/api/cloud-access/dropbox/connect";

    /// <inheritdoc />
    public ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder)
        => new DropboxConnection(folder.Data, identity: identity);
}
