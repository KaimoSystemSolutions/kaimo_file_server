using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Persistence for <see cref="SyncDevice"/> registrations of the client API.
    /// </summary>
    public interface ISyncDeviceRepository
    {
        /// <summary>Returns the device by id, or <c>null</c> if it does not exist.</summary>
        Task<SyncDevice?> GetByIdAsync(Guid deviceId);

        /// <summary>Returns all (including revoked) devices owned by a user, newest first.</summary>
        Task<List<SyncDevice>> GetByUserAsync(Guid userId);

        /// <summary>Persists a new device registration.</summary>
        Task<SyncDevice> CreateAsync(SyncDevice device);

        /// <summary>Updates a device's mutable fields (display name, platform, push token, last-seen, revoked).</summary>
        Task UpdateAsync(SyncDevice device);

        /// <summary>Stamps <see cref="SyncDevice.LastSeenUtc"/> without loading the aggregate first.</summary>
        Task TouchLastSeenAsync(Guid deviceId, DateTime whenUtc);
    }
}
