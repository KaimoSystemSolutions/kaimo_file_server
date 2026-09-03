namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Thrown when a write is attempted while the process runs in read-only demo mode
/// (env var <c>KAIMO_DEMO_READONLY=true</c>). Surfaces at the two write chokepoints:
/// the EF SaveChanges interceptor (all DB + settings) and the ACL guard (files).
/// </summary>
public sealed class ReadOnlyDemoException : Exception
{
    public ReadOnlyDemoException()
        : base("This server runs in read-only demo mode; changes are not permitted.") { }
}
