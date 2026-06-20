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

        public AuthenticationLookup(
            IUserRepository userRepo,
            IShareRepository shareRepo,
            IAclService aclService,
            IUserContextFactory contextFactory)
        {
            _userRepo = userRepo;
            _shareRepo = shareRepo;
            _aclService = aclService;
            _contextFactory = contextFactory;
        }

        public async Task<byte[]?> GetNtHashAsync(string username)
        {
            var user = await _userRepo.GetByUsernameAsync(username);
            if (user == null) return null;
            return Convert.FromHexString(user.NtHash);
        }

        public async Task<UserContext?> ResolveUserContextAsync(string username)
        {
            var user = await _userRepo.GetByUsernameAsync(username);
            if (user == null) return null;
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
