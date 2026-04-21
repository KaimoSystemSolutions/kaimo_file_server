using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Smb
{
    public class UserContextAccessor : IUserContextAccessor
    {
        private static readonly AsyncLocal<UserContext> _current = new();

        public UserContext Get() => _current.Value;

        public void Set(UserContext context) => _current.Value = context;
    }
}
