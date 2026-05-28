using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Many-to-many join between <see cref="User"/> and <see cref="Role"/>.
    /// Composite key: (<see cref="UserId"/>, <see cref="RoleId"/>).
    /// </summary>
    public class UserRole
    {
        public Guid UserId { get; set; }
        public Guid RoleId { get; set; }

        /// <summary>EF Core constructor.</summary>
        protected UserRole() { }

        /// <exception cref="ArgumentException">
        /// Thrown when either id is <see cref="Guid.Empty"/>.
        /// </exception>
        public UserRole(Guid userId, Guid roleId)
        {
            UserId = userId != Guid.Empty
                ? userId
                : throw new ArgumentException("Must not be empty.", nameof(userId));

            RoleId = roleId != Guid.Empty
                ? roleId
                : throw new ArgumentException("Must not be empty.", nameof(roleId));
        }
    }
}