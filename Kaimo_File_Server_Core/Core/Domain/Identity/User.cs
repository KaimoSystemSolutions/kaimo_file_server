using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain.Identity
{
    public class User : Identity
    {
        public string Username { get; init; }

        public User(Guid id, string name, string username) : base(id, name)
        {
            Username = username;
        }
    }
}
