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

    /// <summary>
    /// Evicts the matching cached connection and closes it WITHOUT revoking the grant.
    /// Safe to call after a re-authorization so the next use rebuilds with fresh
    /// credentials instead of serving a stale connection until the process restarts.
    /// </summary>
    Task EvictAsync(Guid shareId, SyncedFolder folder);

    /// <summary>Evicts and closes every cached connection for a share (e.g. on share delete).</summary>
    Task EvictShareAsync(Guid shareId);

    /// <summary>Evicts and closes every cached connection carrying a given connection id.</summary>
    Task EvictConnectionAsync(Guid connectionId);

    /// <summary>
    /// Revokes the grant at the provider and then evicts the connection. Only for a
    /// deliberate user-initiated disconnect — never for cache maintenance.
    /// </summary>
    Task RevokeAndEvictAsync(Guid shareId, SyncedFolder folder);
}
