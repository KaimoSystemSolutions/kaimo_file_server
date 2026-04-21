using static Kaimo_File_Server_Core.Core.Security.FilePermissions;

namespace Kaimo_File_Server_Core.Core.Security
{
    public class AccessEntry
    {
        public Guid PrincipalId { get; init; } // user or group
        public FilePermission Permissions { get; init; }
    }
}
