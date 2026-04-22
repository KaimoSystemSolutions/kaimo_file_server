using Kaimo_File_Server_Core.Core.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Repositories
{
    public interface IShareAccessRepository
    {
        Task<bool> HasAccessAsync(string shareName, Guid principalId);
        Task<List<ShareAccessEntry>> GetByShareAsync(string shareName);
    }
}
