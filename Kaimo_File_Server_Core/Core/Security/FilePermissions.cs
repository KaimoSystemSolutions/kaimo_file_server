using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Security
{
    public class FilePermissions
    {
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
        /// (permissions & FilePermission.Read) != 0
        /// </summary>
        [Flags]
        public enum FilePermission
        {
            None = 0,
            Read = 1 << 0, // Datei lesen
            Write = 1 << 1, // Datei ändern/überschreiben
            Delete = 1 << 2, // Datei löschen
            Create = 1 << 3, // neue Dateien/Ordner erstellen
            List = 1 << 4, // Verzeichnisinhalt sehen
            Execute = 1 << 5, // relevant für ausführbare Dateien
            ChangeAcl = 1 << 6, // Berechtigungen ändern
            FullControl = ~0
        }
    }
}
