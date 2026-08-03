using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Resolves registered providers and caches live connections by immutable
/// credential identity rather than mutable synchronization settings.
/// </summary>
public interface ICloudProviderFactory
{
    /// <summary>All provider registrations available to the UI.</summary>
    IReadOnlyCollection<ICloudProvider> Providers { get; }

    /// <summary>Returns a cached connection or creates it from persisted credentials.</summary>
    ICloudConnection CreateOrLoad(Guid shareId, SyncedFolder folder);

    /// <summary>Removes and disposes the matching authenticated connection.</summary>
    Task DisposeConnectionAsync(Guid shareId, SyncedFolder folder);
}
