using System;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Grants a security principal (user or group) access to connect to
    /// a named share. This is the share-level gate evaluated before any
    /// file-level ACL check occurs.
    ///
    /// Without a matching <see cref="ShareAccessEntry"/>, a principal
    /// cannot even see the share in directory listings.
    /// </summary>
    public class ShareAccessEntry
    {
        /// <summary>Unique identifier for this access entry.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Name of the share this entry applies to.
        /// Must match <see cref="ShareDefinition.Name"/>.
        /// </summary>
        public string ShareName { get; set; } = string.Empty;

        /// <summary>
        /// Id of the <see cref="Identity.User"/> or <see cref="Identity.Group"/>
        /// being granted access.
        /// </summary>
        public Guid PrincipalId { get; set; }

        /// <summary>EF Core / serialization constructor.</summary>
        internal ShareAccessEntry() { }

        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="shareName"/> is blank or
        /// <paramref name="principalId"/> is <see cref="Guid.Empty"/>.
        /// </exception>
        public ShareAccessEntry(string shareName, Guid principalId)
        {
            Id = Guid.NewGuid();

            ShareName = !string.IsNullOrWhiteSpace(shareName)
                ? shareName
                : throw new ArgumentException("Share name must not be empty.", nameof(shareName));

            PrincipalId = principalId != Guid.Empty
                ? principalId
                : throw new ArgumentException("Must not be empty.", nameof(principalId));
        }
    }
}