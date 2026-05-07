using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class Role : Identity
    {

        protected Role() { }

        public Role(Guid id, string name) : base(id, name)
        {
        }
    }
}
