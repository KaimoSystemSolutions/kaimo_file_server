using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class UserRole
    {
        public Guid UserId { get; set; }
        public Guid RoleId { get; set; }

        protected UserRole() { }

        public UserRole(Guid userId, Guid roleId)
        {
            UserId = userId;
            RoleId = roleId;
        }
    }
}
