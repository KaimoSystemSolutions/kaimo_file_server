using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// A web-login JWT that was signed out before its expiry. The token itself is not
    /// stored — only its unique id (<c>jti</c>) — so a copy taken from the browser can no
    /// longer establish a session after logout. Rows are pruned once
    /// <see cref="ExpiresAtUtc"/> has passed, when the token is unusable anyway.
    /// </summary>
    public sealed class RevokedWebToken
    {
        /// <summary>The token's <c>jti</c> claim.</summary>
        public string Jti { get; set; } = string.Empty;

        /// <summary>The token's own expiry (<c>exp</c>); the row is kept until then.</summary>
        public DateTime ExpiresAtUtc { get; set; }
    }
}
