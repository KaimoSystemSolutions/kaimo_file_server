using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Helpers
{
    public static class WellKnownGUIDs
    {
        public static readonly Guid DEPARTMENT_GLOBAL =         Guid.Parse("00000000-0000-0000-0000-000000000001");
        public static readonly Guid ROLE_USER =                 Guid.Parse("00000000-0000-0000-0000-100000000000");
        public static readonly Guid ROLE_ADMIN =                Guid.Parse("00000000-0000-0000-0000-100000000001");
        public static readonly Guid ROLE_USER_MANAGER =         Guid.Parse("00000000-0000-0000-0000-100000000002");
        public static readonly Guid ROLE_SHARE_MANAGER =        Guid.Parse("00000000-0000-0000-0000-100000000003");
        public static readonly Guid ROLE_DEPARTMENT_ADMIN =     Guid.Parse("00000000-0000-0000-0000-100000000004");
        public static readonly Guid ROLE_CERTIFICATE_MANAGER =  Guid.Parse("00000000-0000-0000-0000-100000000005");
        public static readonly Guid ROLE_SYNC_MANAGER =         Guid.Parse("00000000-0000-0000-0000-100000000006");
    }
}
