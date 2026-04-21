using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain.Identity
{
    public class Group : Identity
    {
        public Group(Guid id, string name) : base(id, name)
        {
        }
    }
}
