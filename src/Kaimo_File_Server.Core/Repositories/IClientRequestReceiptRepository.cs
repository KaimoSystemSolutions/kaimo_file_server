using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Persistence for idempotency receipts (<see cref="ClientRequestReceipt"/>) that
    /// let a retried mutating client-API request replay its original outcome.
    /// </summary>
    public interface IClientRequestReceiptRepository
    {
        /// <summary>
        /// Returns the receipt for a device's idempotency key, or <c>null</c> if this
        /// is the first time the key is seen.
        /// </summary>
        Task<ClientRequestReceipt?> GetAsync(Guid deviceId, string idempotencyKey);

        /// <summary>
        /// Inserts a new receipt. Returns <c>false</c> when another concurrent request
        /// already inserted one for the same <c>(DeviceId, IdempotencyKey)</c> (unique
        /// constraint violation), so the caller can fall back to replaying that one.
        /// </summary>
        Task<bool> TryInsertAsync(ClientRequestReceipt receipt);

        /// <summary>Deletes receipts older than <paramref name="cutoffUtc"/>; returns the count removed.</summary>
        Task<int> PruneOlderThanAsync(DateTime cutoffUtc);
    }
}
