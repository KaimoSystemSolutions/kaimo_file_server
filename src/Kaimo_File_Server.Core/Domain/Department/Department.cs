using System;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Organizational unit that scopes administrative permissions and
    /// provides default file-level access for its members.
    ///
    /// A department groups <see cref="Identity.User"/>s,
    /// <see cref="Identity.Group"/>s, and <see cref="ShareDefinition"/>s
    /// together. Scoped role assignments reference a department to limit
    /// what a delegated administrator can manage.
    ///
    /// Departments can be nested via <see cref="ParentDepartmentId"/>
    /// to model organisational hierarchies (e.g. "Engineering → Backend").
    /// Inheritance follows OOP semantics: a child department can choose
    /// which parent to inherit from, and can override inherited defaults.
    ///
    /// <see cref="DefaultFilePermission"/> defines the baseline file access
    /// that all department members receive on department-assigned shares.
    /// If null, the effective permission is inherited from the parent chain.
    /// </summary>
    public class Department
    {
        public Guid Id { get; set; }

        /// <summary>Short display name (e.g. "Marketing", "Buchhaltung").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional longer description shown in admin UIs.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Foreign key to the parent department (OOP-style inheritance).
        /// <c>null</c> indicates a top-level (root) department.
        /// A child inherits admin scope and default permissions from its parent
        /// unless explicitly overridden.
        /// </summary>
        public Guid? ParentDepartmentId { get; set; }

        /// <summary>
        /// Default file-level permissions applied to all department members
        /// on all shares assigned to this department.
        ///
        /// <c>null</c> means "inherit from parent department".
        /// If set, overrides the parent's default for this department and
        /// all descendants that don't define their own override.
        ///
        /// Stored as nullable long mapping to <see cref="FilePermission"/> flags.
        /// </summary>
        public long? DefaultFilePermission { get; set; }

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