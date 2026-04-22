using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;
using static Kaimo_File_Server_Core.Core.Security.FilePermission;

namespace Kaimo_File_Server_Core.Core.Security
{
    public interface IAclService
    {
        bool HasAccess(UserContext user, FileMetadata file, FilePermission permission);
        List<AccessEntry> GetEffectiveAcl(List<AccessEntry> parentAcl, bool isDirectory);
    }
}
