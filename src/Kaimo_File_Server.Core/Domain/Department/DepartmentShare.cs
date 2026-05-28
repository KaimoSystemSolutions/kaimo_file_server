using System;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Many-to-many join between <see cref="Department"/> and
    /// <see cref="ShareDefinition"/>.
    /// Composite key: (<see cref="DepartmentId"/>, <see cref="ShareId"/>).
    ///
    /// A share may belong to multiple departments (e.g. a company-wide
    /// "General" folder), and department-scoped administrators can only
    /// manage shares assigned to their own department.
    /// </summary>
    public class DepartmentShare
    {
        public Guid DepartmentId { get; set; }
        public Guid ShareId { get; set; }

        /// <summary>EF Core constructor.</summary>
        internal DepartmentShare() { }

        /// <exception cref="ArgumentException">
        /// Thrown when either id is <see cref="Guid.Empty"/>.
        /// </exception>
        public DepartmentShare(Guid departmentId, Guid shareId)
        {
            DepartmentId = departmentId != Guid.Empty
                ? departmentId
                : throw new ArgumentException("Must not be empty.", nameof(departmentId));

            ShareId = shareId != Guid.Empty
                ? shareId
                : throw new ArgumentException("Must not be empty.", nameof(shareId));
        }
    }
}