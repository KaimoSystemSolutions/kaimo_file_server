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

        /// <summary>
        /// Returns every device registration across all users, newest-seen first.
        /// Intended for the admin management surface; callers must enforce the
        /// <see cref="Core.Security.ManagementPermission.ManageClientDevices"/> permission.
        /// </summary>
        Task<List<SyncDevice>> GetAllAsync();

        /// <summary>Persists a new device registration.</summary>
        Task<SyncDevice> CreateAsync(SyncDevice device);

        /// <summary>Updates a device's mutable fields (display name, platform, push token, last-seen, revoked).</summary>
        Task UpdateAsync(SyncDevice device);

        /// <summary>Stamps <see cref="SyncDevice.LastSeenUtc"/> without loading the aggregate first.</summary>
        Task TouchLastSeenAsync(Guid deviceId, DateTime whenUtc);

        /// <summary>
        /// Permanently removes a single device registration by id. Its refresh tokens, sync
        /// selections and idempotency receipts are removed by their ON DELETE CASCADE foreign
        /// keys. Returns <c>true</c> if a device was deleted. Callers must enforce the
        /// <see cref="Core.Security.ManagementPermission.ManageClientDevices"/> permission.
        /// </summary>
        Task<bool> DeleteAsync(Guid deviceId);

        /// <summary>
        /// Permanently removes device registrations that can no longer have access, so the
        /// management surface is not cluttered with dead entries indefinitely. A device is
        /// retired when either:
        /// <list type="bullet">
        /// <item>it was revoked before <paramref name="revokedBeforeUtc"/> (revocation grace period elapsed), or</item>
        /// <item>it has not been seen since <paramref name="inactiveBeforeUtc"/> — its refresh tokens have
        /// long expired, so it must sign in from scratch (a fresh registration) to regain access.</item>
        /// </list>
        /// Deleting a device cascades to its refresh tokens, sync selections and idempotency
        /// receipts via the database foreign keys. Returns the number of devices removed.
        /// </summary>
        Task<int> DeleteRetiredAsync(DateTime revokedBeforeUtc, DateTime inactiveBeforeUtc);
    }
}
