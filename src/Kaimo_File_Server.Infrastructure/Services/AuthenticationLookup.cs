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

            // If a share is hidden, it should not be visible to anyone, even if they have permissions to access it.
            if (share.IsShareHidden)
                return false;

            var user = await _contextFactory.CreateByUserIdAsync(principalId);
            if (user is null) 
                return false;

            // If user has access to list the share, they should be able to see it in the share list.
            if ( await _aclService.HasAccessAsync(user, shareID, "", true, FilePermission.ListReadData))
                return true;

            return false;
        }
    }
}
