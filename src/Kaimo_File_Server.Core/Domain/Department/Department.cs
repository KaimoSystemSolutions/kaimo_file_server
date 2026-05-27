using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Organizational unit that scopes administrative permissions.
    /// 
    /// A Department groups Users, Groups, and Shares together.
    /// Scoped role assignments reference a Department to limit
    /// what a delegated admin can manage.
    /// 
    /// Examples: "Entwicklung", "Marketing", "Buchhaltung"
    /// </summary>
    public class Department
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }

        /// <summary>
        /// Optional parent for hierarchical departments.
        /// null = top-level department.
        /// </summary>
        public Guid? ParentDepartmentId { get; set; }

        internal Department() { }

        public Department(string name, string? description = null, Guid? parentDepartmentId = null)
        {
            Id = Guid.NewGuid();
            Name = name;
            Description = description;
            ParentDepartmentId = parentDepartmentId;
        }
    }
}
