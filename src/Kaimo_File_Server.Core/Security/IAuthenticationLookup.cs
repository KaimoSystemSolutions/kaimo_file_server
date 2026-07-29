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
        /// Reads and validates one bounded page of active Samba credentials.
        /// Invalid stored credentials are isolated instead of aborting the
        /// complete synchronization.
        /// </summary>
        Task<SambaCredentialBatch> GetSambaCredentialBatchAsync(
            int offset,
            int pageSize,
            CancellationToken cancellationToken);

        /// <summary>
        /// Resolves a username to a fully populated UserContext (with groups, roles, permissions).
        /// Returns null if the user doesn't exist.
        /// </summary>
        Task<UserContext?> ResolveUserContextAsync(string username);

        /// <summary>
        /// Checks whether a principal (user or group) can SEE a share in a listing.
        /// Returns false for hidden shares — use this for share enumeration (ABE).
        /// </summary>
        Task<bool> CanListShareAsync(Guid shareID, Guid principalId);

        /// <summary>
        /// Checks whether a principal (user or group) may CONNECT to / access a share.
        /// Unlike <see cref="CanListShareAsync"/> this does NOT consider the hidden
        /// flag: a hidden share stays reachable via its direct path/link as long as
        /// the principal holds the required ACL on the share root.
        /// </summary>
        Task<bool> CanAccessShareAsync(Guid shareID, Guid principalId);
    }

    public sealed record SambaCredential(
        string Username,
        byte[] NtHash);

    public sealed record SambaCredentialBatch(
        IReadOnlyList<SambaCredential> Credentials,
        IReadOnlyList<string> RejectedUsernames,
        int SourceCount,
        bool HasMore);
}
