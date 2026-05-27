using Kaimo_File_Server.Core.Domain.Identity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IUserContextFactory
    {
        Task<UserContext> CreateAsync(User user);
        Task<UserContext?> CreateByUsernameAsync(string username);
    }
}
