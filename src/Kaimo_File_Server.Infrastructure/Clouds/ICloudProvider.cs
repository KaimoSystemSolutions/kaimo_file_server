using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Extension point for a cloud provider. Adding another service only requires a
/// provider implementation and one DI registration; consumers never need a new
/// provider-specific switch statement.
/// </summary>
public interface ICloudProvider
{
    /// <summary>Stable identifier persisted in <see cref="SyncedFolder.Provider"/>.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the administration UI.</summary>
    string DisplayName { get; }

    /// <summary>Application endpoint that starts this provider's authorization flow.</summary>
    string AuthorizationEndpoint { get; }

    ICloudConnection CreateConnection(Guid shareId, SyncedFolder folder);
}
