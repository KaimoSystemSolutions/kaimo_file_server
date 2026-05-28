using System;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Many-to-many join between <see cref="Department"/> and
    /// <see cref="Identity.User"/>.
    /// Composite key: (<see cref="DepartmentId"/>, <see cref="UserId"/>).
    ///
    /// A user can belong to multiple departments. This membership is
    /// evaluated by <c>ManagementAuthService</c> when checking whether
    /// a delegated admin has authority over a given user.
    /// </summary>
    public class DepartmentUser
    {
        public Guid DepartmentId { get; set; }
        public Guid UserId { get; set; }

        /// <summary>EF Core constructor.</summary>
        internal DepartmentUser() { }

        /// <exception cref="ArgumentException">
        /// Thrown when either id is <see cref="Guid.Empty"/>.
        /// </exception>
        public DepartmentUser(Guid departmentId, Guid userId)
        {
            DepartmentId = departmentId != Guid.Empty
                ? departmentId
                : throw new ArgumentException("Must not be empty.", nameof(departmentId));

            UserId = userId != Guid.Empty
                ? userId
                : throw new ArgumentException("Must not be empty.", nameof(userId));
        }
    }
}