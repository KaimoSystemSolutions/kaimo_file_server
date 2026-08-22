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

        /// <summary>Persists mutable fields of an existing token (revocation, rotation link).</summary>
        Task UpdateAsync(RefreshToken token);

        /// <summary>
        /// Atomically rotates a token: revokes <paramref name="current"/> (linking
        /// it to the replacement) and inserts <paramref name="replacement"/> in one
        /// transaction, so a crash cannot leave the chain half-updated.
        /// </summary>
        Task RotateAsync(RefreshToken current, RefreshToken replacement);

        /// <summary>Revokes every active token for a device (device logout/revocation).</summary>
        Task RevokeAllForDeviceAsync(Guid deviceId, DateTime whenUtc);

        /// <summary>Revokes every active token for a user (global sign-out).</summary>
        Task RevokeAllForUserAsync(Guid userId, DateTime whenUtc);
    }
}
