using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain.Identity
{
    public class User : Identity
    {
        public string Username { get; init; }
        // General Hash
        public string PasswordHash { get; init; }
        // Just for SMB NTLM Authentication
        public string NtHash { get; init; }

        protected User() { }

        public User(Guid id, string name, string username, string passwordHash, string ntHash) : base(id, name)
        {
            Username = username;
            PasswordHash = passwordHash;
            NtHash = ntHash;
        }
    }
}
