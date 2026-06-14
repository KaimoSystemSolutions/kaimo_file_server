using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Security
{
    /// <summary>
    /// Abstracts user lookup and authentication for transport adapters (SMB, HTTP, NFS).
    /// 
    /// This lives in Core so that transport projects (e.g. Smb) can resolve
    /// users and build UserContexts without depending on Infrastructure.
    /// The implementation lives in Infrastructure and is wired up by the Host.
    /// </summary>
    public interface IAuthenticationLookup
    {
        /// <summary>
        /// Returns the raw NT-Hash bytes for NTLM authentication, or null if the user doesn't exist.
        /// </summary>
        Task<byte[]?> GetNtHashAsync(string username);

        /// <summary>
        /// Resolves a username to a fully populated UserContext (with groups, roles, permissions).
        /// Returns null if the user doesn't exist.
        /// </summary>
        Task<UserContext?> ResolveUserContextAsync(string username);

        /// <summary>
        /// Checks whether a principal (user or group) can list a specific share.
        /// </summary>
        Task<bool> HasShareAccessAsync(Guid shareID, Guid principalId);
    }
}
