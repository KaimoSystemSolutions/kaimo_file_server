using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Many-to-many join between <see cref="User"/> and <see cref="Group"/>.
    /// Composite key: (<see cref="UserId"/>, <see cref="GroupId"/>).
    /// </summary>
    public class UserGroup
    {
        public Guid UserId { get; set; }
        public Guid GroupId { get; set; }

        /// <summary>EF Core constructor.</summary>
        protected UserGroup() { }

        /// <exception cref="ArgumentException">
        /// Thrown when either id is <see cref="Guid.Empty"/>.
        /// </exception>
        public UserGroup(Guid userId, Guid groupId)
        {
            UserId = userId != Guid.Empty
                ? userId
                : throw new ArgumentException("Must not be empty.", nameof(userId));

            GroupId = groupId != Guid.Empty
                ? groupId
                : throw new ArgumentException("Must not be empty.", nameof(groupId));
        }
    }
}