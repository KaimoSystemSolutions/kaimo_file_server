using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Factory for building a <see cref="UserContext"/> that bundles a user's
    /// identity together with resolved group memberships and effective roles.
    /// </summary>
    public interface IUserContextFactory
    {
        /// <summary>
        /// Creates a fully resolved context for the given user entity.
        /// </summary>
        /// <param name="user">A user that has already been loaded from the database.</param>
        Task<UserContext> CreateAsync(User user);

        /// <summary>
        /// Looks up a user by username and, if found, creates the resolved context.
        /// Returns <c>null</c> when the username does not exist.
        /// </summary>
        /// <param name="username">The login name to look up.</param>
        Task<UserContext?> CreateByUsernameAsync(string username);

        /// <summary>
        /// Looks up a user by userID and, if found, creates the resolved context.
        /// Returns <c>null</c> when the user does not exist.
        /// </summary>
        /// <param name="userId">The unique identifier for the user.</param>
        Task<UserContext?> CreateByUserIdAsync(Guid userId);
    }
}