namespace Kaimo_File_Server.Core.Security;

/// <summary>Transport a login attempt or client request arrived through.</summary>
public enum SecurityChannel
{
    Web,
    Api,
    WebDav,
}
