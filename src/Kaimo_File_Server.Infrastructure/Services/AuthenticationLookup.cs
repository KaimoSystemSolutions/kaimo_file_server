using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Infrastructure.Services
{
    /// <summary>
    /// Infrastructure implementation of IAuthenticationLookup.
    /// Uses the existing repositories and UserContextFactory to fulfill
    /// the contract that transport layers (SMB, HTTP, NFS) depend on.
    /// </summary>
    public class AuthenticationLookup : IAuthenticationLookup
    {
        private readonly IUserRepository _userRepo;
        private readonly IShareRepository _shareRepo;
        private readonly IAclService _aclService;
        private readonly IUserContextFactory _contextFactory;
        private readonly INtHashProtector _ntHashProtector;

        /// <summary>
        /// NT hash of the empty password (MD4 of an empty UTF-16LE string, the well-known
        /// 31D6CFE0D16AE931B73C59D7E0C089C0). Computed once so we can reject any account that
        /// effectively has no password — "no login without a password".
        /// </summary>
        private readonly string _emptyPasswordNtHash;

        public AuthenticationLookup(
            IUserRepository userRepo,
            IShareRepository shareRepo,
            IAclService aclService,
            IUserContextFactory contextFactory,
            IPasswordService passwordService,
            INtHashProtector ntHashProtector)
        {
            _userRepo = userRepo;
            _shareRepo = shareRepo;
            _aclService = aclService;
            _contextFactory = contextFactory;
            _ntHashProtector = ntHashProtector;
            _emptyPasswordNtHash = passwordService.ComputeNtHash(string.Empty);
        }

        public async Task<byte[]?> GetNtHashAsync(string username)
        {
            var user = await _userRepo.GetByUsernameAsync(username);
            if (user == null) return null;

            // Disabled accounts must never authenticate, regardless of correct credentials.
            if (!user.IsEnabled) return null;

            // Decrypt the at-rest NT hash (legacy plaintext rows pass through unchanged).
            string ntHashHex = _ntHashProtector.Unprotect(user.NtHash);

            // No login without a password: refuse the well-known empty-password NT hash so an
            // account that ended up with a blank password can never authenticate over SMB/NTLM
            // (a blank-password NTLM bind would otherwise match and succeed).
            if (string.Equals(ntHashHex, _emptyPasswordNtHash, StringComparison.OrdinalIgnoreCase))
                return null;

            byte[] hash = Convert.FromHexString(ntHashHex);
            return hash.Length == 16 ? hash : null;
        }

        public async Task<SambaCredentialBatch> GetSambaCredentialBatchAsync(
            int offset,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
            IReadOnlyList<SambaCredentialSource> sources =
                await _userRepo.GetSambaCredentialBatchAsync(
                    offset,
                    checked(pageSize + 1),
                    cancellationToken);
            bool hasMore = sources.Count > pageSize;
            var credentials = new List<SambaCredential>(pageSize);
            var rejectedUsernames = new List<string>();

            foreach (SambaCredentialSource source in sources.Take(pageSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Kaimo_File_Server.Core.Helpers.SambaName.IsValidUsername(
                        source.Username))
                {
                    rejectedUsernames.Add(source.Username);
                    continue;
                }

                try
                {
                    string ntHashHex =
                        _ntHashProtector.Unprotect(source.StoredNtHash);
                    if (string.Equals(
                            ntHashHex,
                            _emptyPasswordNtHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    byte[] hash = Convert.FromHexString(ntHashHex);
                    if (hash.Length != 16)
                    {
                        rejectedUsernames.Add(source.Username);
                        continue;
                    }

                    credentials.Add(new SambaCredential(
                        source.Username,
                        hash));
                }
                catch (Exception exception) when (
                    exception is FormatException
                    or ArgumentException
                    or System.Security.Cryptography.CryptographicException)
                {
                    rejectedUsernames.Add(source.Username);
                }
            }

            return new SambaCredentialBatch(
                credentials,
                rejectedUsernames,
                Math.Min(sources.Count, pageSize),
                hasMore);
        }

        public async Task<UserContext?> ResolveUserContextAsync(string username)
        {
            var user = await _userRepo.GetByUsernameAsync(username);
            if (user == null || !user.IsEnabled) return null;
            return await _contextFactory.CreateAsync(user);
        }

        public async Task<bool> CanListShareAsync(Guid shareID, Guid principalId)
        {
            ShareDefinition? share = await _shareRepo.GetByIdAsync(shareID);
            if (share is null)
                return false;

            // If a share is hidden, it should not be visible in any listing,
            // even if the principal has the permissions to access it directly.
            if (share.IsShareHidden)
                return false;

            return await HasRootListAccessAsync(shareID, principalId);
        }

        public async Task<bool> CanAccessShareAsync(Guid shareID, Guid principalId)
        {
            ShareDefinition? share = await _shareRepo.GetByIdAsync(shareID);
            if (share is null)
                return false;

            // Connect/access path intentionally ignores IsShareHidden: a hidden
            // share stays reachable via its direct path as long as the ACL allows.
            return await HasRootListAccessAsync(shareID, principalId);
        }

        private async Task<bool> HasRootListAccessAsync(Guid shareID, Guid principalId)
        {
            var user = await _contextFactory.CreateByUserIdAsync(principalId);
            if (user is null)
                return false;

            return await _aclService.HasAccessAsync(
                user, shareID, "", true, FilePermission.ListReadData);
        }
    }
}
