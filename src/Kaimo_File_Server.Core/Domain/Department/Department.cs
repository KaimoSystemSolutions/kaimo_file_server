using System;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Organizational unit that scopes administrative permissions.
    ///
    /// A department groups <see cref="Identity.User"/>s,
    /// <see cref="Identity.Group"/>s, and <see cref="ShareDefinition"/>s
    /// together. Scoped role assignments reference a department to limit
    /// what a delegated administrator can manage.
    ///
    /// Departments can be nested via <see cref="ParentDepartmentId"/>
    /// to model organisational hierarchies (e.g. "Engineering → Backend").
    /// </summary>
    public class Department
    {
        public Guid Id { get; set; }

        /// <summary>Short display name (e.g. "Marketing", "Buchhaltung").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional longer description shown in admin UIs.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Foreign key to the parent department.
        /// <c>null</c> indicates a top-level (root) department.
        /// </summary>
        public Guid? ParentDepartmentId { get; set; }

        /// <summary>EF Core constructor.</summary>
        internal Department() { }

        /// <param name="name">Display name — must not be blank.</param>
        /// <param name="description">Optional description.</param>
        /// <param name="parentDepartmentId">
        /// Parent department id, or <c>null</c> for a root department.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> is null or whitespace.
        /// </exception>
        public Department(string name, string? description = null, Guid? parentDepartmentId = null)
        {
            Id = Guid.NewGuid();
            Name = !string.IsNullOrWhiteSpace(name)
                ? name
                : throw new ArgumentException("Name must not be empty.", nameof(name));
            Description = description;
            ParentDepartmentId = parentDepartmentId;
        }
    }
}