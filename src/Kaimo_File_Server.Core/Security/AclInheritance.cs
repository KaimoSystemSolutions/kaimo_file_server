using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Security
{
    [Flags]
    public enum AclInheritance
    {
        None = 0,
        ThisFolder = 1 << 0,
        SubFolders = 1 << 1,
        SubFiles = 1 << 2,
        AllDescendants = 1 << 3,  // rekursiv alles darunter

        // Shortcuts
        ThisOnly = ThisFolder,
        ThisAndDirect = ThisFolder | SubFolders | SubFiles,
        Everything = ThisFolder | SubFolders | SubFiles | AllDescendants
    }
}
