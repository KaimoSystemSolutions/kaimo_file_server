using Kaimo_File_Server.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IShareAccessRepository
    {
        Task<bool> HasAccessAsync(string shareName, Guid principalId);
        Task<List<ShareAccessEntry>> GetByShareAsync(string shareName);
        Task GrantAccessAsync(string shareName, Guid principalId);
    }
}
