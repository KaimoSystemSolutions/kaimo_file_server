using Kaimo_File_Server.Core.Security;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Web.Helpers
{
    /// <summary>
    /// Display metadata for file permission flags used in the department
    /// default ACL configuration UI. Purely presentational — maps
    /// FilePermission flags to human-readable labels for the admin panel.
    /// </summary>
    public static class DepartmentPermissionDisplay
    {
        public record FlagInfo(long Flag, string Label, string Description);

        public static readonly FlagInfo[] AllFlags =
        [
            new((long)FilePermission.ListReadData,    "Lesen",     "Dateien und Ordner anzeigen und lesen"),
            new((long)FilePermission.CreateWriteData, "Schreiben", "Dateien und Ordner erstellen und bearbeiten"),
            new((long)FilePermission.Delete,          "Löschen",   "Dateien und Ordner löschen"),
        ];

        public static bool HasFlag(long permission, long flag)
            => (permission & flag) == flag;

        public static long SetFlag(long permission, long flag, bool value)
            => value ? (permission | flag) : (permission & ~flag);

    }
}
