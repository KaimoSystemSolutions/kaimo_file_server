using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class UserGroup
    {
        public Guid UserId { get; set; }
        public Guid GroupId { get; set; }

        protected UserGroup() { }

        public UserGroup(Guid userId, Guid groupId)
        {
            UserId = userId;
            GroupId = groupId;
        }
    }
}
