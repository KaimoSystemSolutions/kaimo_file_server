using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

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
