using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Repositories
{
    public interface IUserContextFactory
    {
        Task<UserContext> CreateAsync(User user);
    }
}
