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
        private readonly IUserContextFactory _contextFactory;

        public AuthenticationLookup(
            IUserRepository userRepo,
            IShareRepository shareRepo,
            IUserContextFactory contextFactory)
        {
            _userRepo = userRepo;
            _shareRepo = shareRepo;
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

        public async Task<bool> HasShareAccessAsync(Guid shareID, Guid principalId)
        {
            ShareDefinition? share = await _shareRepo.GetByIdAsync(shareID);
            if (share is null)
                return false;

            // If a share is hidden, it should not be visible to anyone, even if they have permissions to access it.
            return !share.IsShareHidden;

        }
    }
}
