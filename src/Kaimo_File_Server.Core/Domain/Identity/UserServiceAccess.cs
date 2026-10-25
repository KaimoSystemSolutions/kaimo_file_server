using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// File services a user may use. Stored per user as a bit set; each service must also be
    /// switched on globally to be usable. Members of the Admins group always keep
    /// <see cref="Web"/> so administrators cannot lock themselves out.
    /// </summary>
    [Flags]
    public enum UserServiceAccess
    {
        None = 0,

        /// <summary>Client app (REST API): sync and browsing.</summary>
        App = 1,

        /// <summary>Web UI login.</summary>
        Web = 2,

        /// <summary>SMB/CIFS shares.</summary>
        Smb = 4,

        /// <summary>WebDAV endpoint.</summary>
        WebDav = 8,

        All = App | Web | Smb | WebDav,
    }
}
