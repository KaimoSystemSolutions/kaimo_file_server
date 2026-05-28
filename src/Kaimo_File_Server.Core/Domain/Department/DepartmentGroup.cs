using System;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Many-to-many join between <see cref="Department"/> and
    /// <see cref="Identity.Group"/>.
    /// Composite key: (<see cref="DepartmentId"/>, <see cref="GroupId"/>).
    ///
    /// Ensures that department-scoped administrators can only manage
    /// groups that have been explicitly assigned to their department.
    /// </summary>
    public class DepartmentGroup
    {
        public Guid DepartmentId { get; set; }
        public Guid GroupId { get; set; }

        /// <summary>EF Core constructor.</summary>
        internal DepartmentGroup() { }

        /// <exception cref="ArgumentException">
        /// Thrown when either id is <see cref="Guid.Empty"/>.
        /// </exception>
        public DepartmentGroup(Guid departmentId, Guid groupId)
        {
            DepartmentId = departmentId != Guid.Empty
                ? departmentId
                : throw new ArgumentException("Must not be empty.", nameof(departmentId));

            GroupId = groupId != Guid.Empty
                ? groupId
                : throw new ArgumentException("Must not be empty.", nameof(groupId));
        }
    }
}