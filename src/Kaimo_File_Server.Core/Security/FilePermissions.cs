using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Security;


/// <summary>
        /// Bitwise combinable file permissions.
        ///
        /// Each permission represents a single bit and can be combined.
        /// Example:
        /// Read (1 << 0)  = 00000001
        /// Write (1 << 1) = 00000010
        ///
        /// Combined:
        /// Read | Write   = 00000011
        ///
        /// Check:
        /// (permissions & FilePermission.ListReadData) != 0
        /// </summary>
[Flags]
public enum FilePermission : long
{
        None = 0,

        // Administration
        ChangePermissions = 1L << 0,
        TakeOwnership = 1L << 1,

        // Lesen
        TraverseExecute = 1L << 2,
        ListReadData = 1L << 3,
        ReadAttributes = 1L << 4,
        ReadExtAttributes = 1L << 5,
        ReadPermissions = 1L << 6,

        // Schreiben
        CreateWriteData = 1L << 7,
        CreateAppendData = 1L << 8,
        WriteAttributes = 1L << 9,
        WriteExtAttributes = 1L << 10,
        DeleteSubItems = 1L << 11,
        Delete = 1L << 12,

        // Kombinations-Shortcuts
        ReadAll = TraverseExecute | ListReadData | ReadAttributes | ReadExtAttributes | ReadPermissions,
        WriteAll = CreateWriteData | CreateAppendData | WriteAttributes | WriteExtAttributes | DeleteSubItems | Delete,
        AdminAll = ChangePermissions | TakeOwnership,
        FullControl = ReadAll | WriteAll | AdminAll
}


