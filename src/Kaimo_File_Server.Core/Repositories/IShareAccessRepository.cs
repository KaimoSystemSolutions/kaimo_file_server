using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IShareAccessRepository
    {
        Task<bool> HasAccessAsync(string shareName, Guid principalId);
        Task<List<ShareAccessEntry>> GetByShareAsync(string shareName);
        Task GrantAccessAsync(string shareName, Guid principalId);
        Task RevokeAccessAsync(string shareName, Guid principalId);

        /// <summary>
        /// Benennt alle AccessEntries von oldName auf newName um (für Share-Rename).
        /// </summary>
        Task UpdateShareNameAsync(string oldName, string newName);
    }
}