using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Persistence for per-device folder sync selections (<see cref="DeviceSyncProfile"/>).
    /// </summary>
    public interface IDeviceSyncProfileRepository
    {
        /// <summary>Returns the profile by id, or <c>null</c> if it does not exist.</summary>
        Task<DeviceSyncProfile?> GetByIdAsync(Guid id);

        /// <summary>Returns all sync selections configured for a device.</summary>
        Task<List<DeviceSyncProfile>> GetByDeviceAsync(Guid deviceId);

        /// <summary>Returns every sync selection a user has configured across all their devices.</summary>
        Task<List<DeviceSyncProfile>> GetByUserAsync(Guid userId);

        /// <summary>Persists a new sync selection.</summary>
        Task<DeviceSyncProfile> CreateAsync(DeviceSyncProfile profile);

        /// <summary>Updates a sync selection's mutable fields (path, mode, enabled).</summary>
        Task UpdateAsync(DeviceSyncProfile profile);

        /// <summary>Deletes a sync selection by id.</summary>
        Task DeleteAsync(Guid id);
    }
}
