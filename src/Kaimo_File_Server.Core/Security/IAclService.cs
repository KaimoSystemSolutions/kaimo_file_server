using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Security
{
    public interface IAclService
    {
        Task<bool> HasAccessAsync(UserContext userContext, Guid shareId,
            string relativePath, bool isDirectory, FilePermission permission);
    }
}