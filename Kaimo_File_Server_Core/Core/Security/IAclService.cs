using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermissions;

namespace Kaimo_File_Server_Core.Core.Security
{
    public interface IAclService
    {
        bool HasAccess(UserContext user, FileMetadata file, FilePermission permission);
    }
}
