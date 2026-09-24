using Kaimo_File_Server.Core.Domain.ClientSync;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Persistence for opaque client-API <see cref="RefreshToken"/>s. Only hashes
    /// are stored; see <see cref="RefreshToken.TokenHash"/>.
    /// </summary>
    public interface IRefreshTokenRepository
    {
        /// <summary>Persists a newly issued refresh token.</summary>
        Task CreateAsync(RefreshToken token);

        /// <summary>Looks up a token by its SHA-256 hash, or <c>null</c> if unknown.</summary>
        Task<RefreshToken?> GetByHashAsync(string tokenHash);

        /// <summary>Looks up a token by its id, or <c>null</c> if unknown. Used to
        /// check the replacement of a rotated token when classifying a replay.</summary>
        Task<RefreshToken?> GetByIdAsync(Guid id);

        /// <summary>Persists mutable fields of an existing token (revocation, rotation link).</summary>
        Task UpdateAsync(RefreshToken token);

        /// <summary>
        /// Atomically rotates a token: revokes <paramref name="current"/> (linking
        /// it to the replacement) and inserts <paramref name="replacement"/> in one
        /// transaction, so a crash cannot leave the chain half-updated. The revoke is
        /// conditional on <paramref name="current"/> still being unrevoked, so of two
        /// concurrent rotations of the same token exactly one wins.
        /// </summary>
        /// <returns><c>false</c> when the token was already revoked (lost race); nothing is written.</returns>
        Task<bool> RotateAsync(RefreshToken current, RefreshToken replacement);

        /// <summary>Revokes every active token for a device (device logout/revocation).</summary>
        Task RevokeAllForDeviceAsync(Guid deviceId, DateTime whenUtc);

        /// <summary>Revokes every active token for a user (global sign-out).</summary>
        Task RevokeAllForUserAsync(Guid userId, DateTime whenUtc);

        /// <summary>
        /// Deletes tokens that expired before <paramref name="cutoffUtc"/>; returns the count removed.
        /// Keyed on <see cref="RefreshToken.ExpiresAtUtc"/>, never on revocation, so the reuse-detection
        /// window (a rotated-then-replayed token) is preserved right up to expiry — an expired token is
        /// already rejected by expiry, so removing it loses nothing.
        /// </summary>
        Task<int> PruneExpiredBeforeAsync(DateTime cutoffUtc);
    }
}
