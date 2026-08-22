using System;

namespace Kaimo_File_Server.Core.Domain.ClientSync
{
    /// <summary>
    /// A long-lived, opaque refresh token issued to a <see cref="SyncDevice"/>.
    ///
    /// Only the SHA-256 hash of the secret is persisted (<see cref="TokenHash"/>);
    /// the plaintext secret is returned to the client exactly once at issue time
    /// and never stored. Presenting a valid refresh token to <c>/auth/refresh</c>
    /// mints a fresh short-lived access token and rotates the refresh token
    /// (the presented one is revoked and a new one is issued), so a leaked token
    /// has a bounded, detectable lifetime.
    ///
    /// This exists because access tokens (JWTs) are intentionally short-lived,
    /// while mobile apps need to stay signed in for a long time without keeping
    /// the user's password.
    /// </summary>
    public sealed class RefreshToken
    {
        /// <summary>Unique identifier for this token record.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>The user the token authenticates.</summary>
        public Guid UserId { get; set; }

        /// <summary>The device the token was issued to.</summary>
        public Guid DeviceId { get; set; }

        /// <summary>
        /// Lowercase hex SHA-256 of the opaque token secret. The plaintext is
        /// never persisted; lookups hash the presented secret and compare.
        /// </summary>
        public string TokenHash { get; set; } = string.Empty;

        /// <summary>UTC timestamp the token was issued.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp after which the token is no longer valid.</summary>
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>
        /// When set, the token has been revoked (via rotation, logout, or device
        /// revocation) and must be rejected.
        /// </summary>
        public DateTime? RevokedAtUtc { get; set; }

        /// <summary>
        /// On rotation, the id of the token that replaced this one. Lets a reuse
        /// of an already-rotated token be detected as a possible theft.
        /// </summary>
        public Guid? ReplacedByTokenId { get; set; }

        /// <summary>Whether the token is currently usable at <paramref name="nowUtc"/>.</summary>
        public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
    }
}
