using System;

namespace Kaimo_File_Server.Core.Domain.ClientSync
{
    /// <summary>
    /// A single client endpoint (a phone, tablet, or desktop app installation)
    /// registered by a user against the client API.
    ///
    /// A device ties together the refresh tokens issued to it
    /// (<see cref="RefreshToken"/>) and the folder sync selections the user has
    /// configured for it (<see cref="DeviceSyncProfile"/>), so a user can later
    /// review and revoke "my phone" from one place.
    ///
    /// This is deliberately distinct from the SMB/Windows client identity and
    /// from the server-to-cloud <c>StorageConnection</c> concept: it models an
    /// end-user device that talks to the REST client API.
    /// </summary>
    public sealed class SyncDevice
    {
        /// <summary>Unique identifier for this device registration.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Owner of the device. All access is scoped to this user's ACLs.</summary>
        public Guid UserId { get; set; }

        /// <summary>User-facing label, e.g. "Marco's Pixel" or "Office Laptop".</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Free-form platform hint reported by the client
        /// (e.g. "android", "ios", "windows", "linux", "macos"). Advisory only.
        /// </summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>
        /// Optional push-notification token (FCM/APNs) used to wake the device on
        /// change. Null until the client registers one. See the notification design.
        /// </summary>
        public string? PushToken { get; set; }

        /// <summary>UTC timestamp the device was first registered.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the most recent authenticated request from the device.</summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When set, the device has been revoked: its refresh tokens are rejected
        /// and it must sign in again to obtain a fresh registration.
        /// </summary>
        public DateTime? RevokedAtUtc { get; set; }

        /// <summary>Whether the device is currently active (not revoked).</summary>
        public bool IsActive => RevokedAtUtc is null;
    }
}
