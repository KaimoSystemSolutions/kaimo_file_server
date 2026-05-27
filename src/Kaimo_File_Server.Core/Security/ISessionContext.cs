using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security
{
    /// <summary>
    /// Represents the security context for a single operation/request.
    /// Each transport adapter creates this per-request and passes it explicitly
    /// through the call chain no ambient state, no AsyncLocal.
    /// 
    /// For SMB: created in OnAccessRequested, stored per-session in the FileHandle
    /// For HTTP: created from the authenticated ClaimsPrincipal per request
    /// For NFS: created from the NFS auth credentials per operation
    /// </summary>
    public interface ISessionContext
    {
        UserContext UserContext { get; }
    }

    /// <summary>
    /// Simple immutable implementation of ISessionContext.
    /// </summary>
    public sealed class SessionContext : ISessionContext
    {
        public UserContext UserContext { get; }

        public SessionContext(UserContext userContext)
        {
            UserContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        }
    }
}