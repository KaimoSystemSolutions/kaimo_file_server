namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Transport a login attempt or client request arrived through. Stored as its number, so new
/// channels are only ever appended.
/// </summary>
public enum SecurityChannel
{
    Web,
    Api,
    WebDav,

    /// <summary>SMB logins, verified by Samba and reported through the SMB bridge.</summary>
    Smb,
}
