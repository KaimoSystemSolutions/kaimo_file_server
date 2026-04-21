using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Smb
{
    public interface IUserContextAccessor
    {
        UserContext Get();
    }
}
