using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Assigns a Group to a Department.
    /// Department admins can only manage groups within their department.
    /// </summary>
    public class DepartmentGroup
    {
        public Guid DepartmentId { get; set; }
        public Guid GroupId { get; set; }

        internal DepartmentGroup() { }

        public DepartmentGroup(Guid departmentId, Guid groupId)
        {
            DepartmentId = departmentId;
            GroupId = groupId;
        }
    }
}
