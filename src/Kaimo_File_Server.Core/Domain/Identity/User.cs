using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// An authenticated user account that can log in and access shares.
    ///
    /// Authentication is supported via two credential types:
    ///   • <see cref="PasswordHash"/> — general-purpose hash (e.g. bcrypt)
    ///     used by the web UI and REST API.
    ///   • <see cref="NtHash"/> — MD4-based hash required for SMB/NTLM
    ///     authentication from Windows clients.
    ///
    /// Both hashes are always stored so the user can authenticate through
    /// any protocol without a separate credential store.
    /// </summary>
    public class User : Identity
    {
        /// <summary>
        /// Unique login name (case-insensitive by convention).
        /// Distinct from <see cref="Identity.Name"/>, which is the display name.
        /// </summary>
        public string Username { get; init; } = string.Empty;

        /// <summary>Optional free-text description shown in admin UIs.</summary>
        public string? Description { get; set; }

        /// <summary>Optional e-mail address for notifications or password resets.</summary>
        public string? Email { get; set; }

        /// <summary>
        /// Optional given name (maps to the AD <c>givenName</c> attribute).
        /// Independent of <see cref="Identity.Name"/>, which remains the authoritative
        /// display name — kept separate so existing name handling stays unchanged.
        /// </summary>
        public string? FirstName { get; set; }

        /// <summary>Optional surname (maps to the AD <c>sn</c> attribute).</summary>
        public string? LastName { get; set; }

        /// <summary>
        /// Optional profile picture bytes. Maps to the AD <c>thumbnailPhoto</c>
        /// attribute (a small JPEG/PNG, typically &lt; 100 KB) so a future LDAP/AD
        /// synchronisation can copy the image across in either direction.
        /// </summary>
        public byte[]? Photo { get; set; }

        /// <summary>MIME type of <see cref="Photo"/> (e.g. <c>image/jpeg</c>), used when rendering.</summary>
        public string? PhotoContentType { get; set; }

        /// <summary>
        /// When <c>false</c>, the account is locked and all authentication
        /// attempts are rejected regardless of correct credentials.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// When <c>false</c>, only an administrator can reset this user's password.
        /// </summary>
        public bool CanChangePassword { get; set; } = true;

        /// <summary>
        /// General-purpose password hash (e.g. bcrypt / Argon2).
        /// Used by the web UI and REST API authentication flow.
        /// </summary>
        public string PasswordHash { get; init; } = string.Empty;

        /// <summary>
        /// NT hash (MD4 of the UTF-16LE password) required for
        /// SMB NTLM authentication from Windows clients.
        /// </summary>
        public string NtHash { get; init; } = string.Empty;

        /// <summary>
        /// Random value embedded in every issued JWT (claim <c>sstamp</c>) and compared on
        /// each validation. Replacing it — on every password change/reset — invalidates all
        /// previously issued web and client-API tokens of the account at once.
        /// </summary>
        public string SecurityStamp { get; set; } = NewSecurityStamp();

        /// <summary>
        /// When <c>true</c> the user must set a new password after signing in (set for the
        /// bootstrap administrator, whose initial password is visible in logs/config).
        /// </summary>
        public bool MustChangePassword { get; set; }

        /// <summary>Creates a fresh, unpredictable <see cref="SecurityStamp"/> value.</summary>
        public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

        /// <summary>EF Core / serialization constructor.</summary>
        protected User() { }

        /// <param name="id">Unique identifier.</param>
        /// <param name="name">Display name.</param>
        /// <param name="username">Login name — must not be blank.</param>
        /// <param name="passwordHash">Pre-computed general-purpose hash.</param>
        /// <param name="ntHash">Pre-computed NT hash for SMB/NTLM.</param>
        /// <param name="description">Optional description.</param>
        /// <param name="email">Optional e-mail address.</param>
        /// <param name="isEnabled">Whether the account is active.</param>
        /// <param name="canChangePassword">Whether the user may change their own password.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="username"/> or either hash is null/whitespace.
        /// </exception>
        public User(
            Guid id,
            string name,
            string username,
            string passwordHash,
            string ntHash,
            string? description = null,
            string? email = null,
            bool isEnabled = true,
            bool canChangePassword = true)
            : base(id, name)
        {
            Kaimo_File_Server.Core.Helpers.SambaName.EnsureValidUsername(
                username, nameof(username));
            Username = username;

            PasswordHash = !string.IsNullOrWhiteSpace(passwordHash)
                ? passwordHash
                : throw new ArgumentException("Password hash must not be empty.", nameof(passwordHash));

            NtHash = !string.IsNullOrWhiteSpace(ntHash)
                ? ntHash
                : throw new ArgumentException("NT hash must not be empty.", nameof(ntHash));

            Description = description;
            Email = email;
            IsEnabled = isEnabled;
            CanChangePassword = canChangePassword;
        }
    }
}
