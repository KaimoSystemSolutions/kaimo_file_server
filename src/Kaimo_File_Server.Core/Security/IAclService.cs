using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Security
{
    public interface IAclService
    {
        Task<bool> HasAccessAsync(UserContext userContext, Guid shareId,
            string relativePath, bool isDirectory, FilePermission permission);

        Task RenameAclPathAsync(Guid shareId, string oldRelativePath, string newRelativePath);
        Task DeleteAclAsync(Guid shareId, string relativePath);
    }
}