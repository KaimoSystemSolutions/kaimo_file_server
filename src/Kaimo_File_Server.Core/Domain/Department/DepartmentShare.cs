using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Assigns a Share to a Department.
    /// Department admins can only manage shares within their department.
    /// A share can belong to multiple departments (e.g. "General" folder).
    /// </summary>
    public class DepartmentShare
    {
        public Guid DepartmentId { get; set; }
        public Guid ShareId { get; set; }

        internal DepartmentShare() { }

        public DepartmentShare(Guid departmentId, Guid shareId)
        {
            DepartmentId = departmentId;
            ShareId = shareId;
        }
    }
}
