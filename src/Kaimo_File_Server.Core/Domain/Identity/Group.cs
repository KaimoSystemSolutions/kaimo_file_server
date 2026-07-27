using System;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// A named collection of users.
    /// Groups are referenced in ACLs (<see cref="Security.AccessEntry"/>)
    /// and share-level access (<see cref="ShareAccessEntry"/>) to grant
    /// permissions to multiple users at once.
    ///
    /// Each group belongs to exactly ONE department via <see cref="DepartmentId"/>.
    /// Groups without explicit assignment default to the Global department.
    /// Users from ANY department can be members of the group — membership
    /// is independent of the group's department assignment.
    /// </summary>
    public class Group : Identity
    {
        /// <summary>
        /// The department this group belongs to.
        /// Determines which department-scoped administrators can manage this group.
        /// Defaults to <see cref="WellKnownGUIDs.DEPARTMENT_GLOBAL"/> (Global department).
        /// A group always belongs to exactly one department.
        /// </summary>
        public Guid DepartmentId { get; set; } = WellKnownGUIDs.DEPARTMENT_GLOBAL;

        /// <summary>EF Core / serialization constructor.</summary>
        protected Group() { }

        /// <param name="id">Unique identifier for this group.</param>
        /// <param name="name">Display name (e.g. "Developers", "HR-Team").</param>
        /// <param name="departmentId">
        /// Department this group belongs to.
        /// Pass <c>null</c> or omit to default to the Global department.
        /// </param>
        public Group(Guid id, string name, Guid? departmentId = null)
            : base(id, name)
        {
            DepartmentId = departmentId ?? WellKnownGUIDs.DEPARTMENT_GLOBAL;
        }
    }
}