namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>Server-side revocation list for signed-out web-login tokens (by <c>jti</c>).</summary>
    public interface IRevokedWebTokenRepository
    {
        /// <summary>Marks the token as revoked until its own expiry. Idempotent.</summary>
        Task RevokeAsync(string jti, DateTime expiresAtUtc);

        /// <summary>Whether the token with this <c>jti</c> was revoked.</summary>
        Task<bool> IsRevokedAsync(string jti);

        /// <summary>Deletes entries whose token expired before <paramref name="cutoffUtc"/>; returns the count.</summary>
        Task<int> PruneExpiredBeforeAsync(DateTime cutoffUtc);
    }
}
