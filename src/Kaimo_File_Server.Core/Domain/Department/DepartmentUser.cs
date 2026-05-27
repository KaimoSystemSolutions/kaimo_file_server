using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Assigns a User to a Department.
    /// A user can belong to multiple departments.
    /// </summary>
    public class DepartmentUser
    {
        public Guid DepartmentId { get; set; }
        public Guid UserId { get; set; }

        internal DepartmentUser() { }

        public DepartmentUser(Guid departmentId, Guid userId)
        {
            DepartmentId = departmentId;
            UserId = userId;
        }
    }
}
