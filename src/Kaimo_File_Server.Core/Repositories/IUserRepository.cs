using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for managing user accounts and their relationship
    /// to groups and roles.
    /// </summary>
    public interface IUserRepository
    {
        // --------------------------------------------
        //  CRUD
        // --------------------------------------------

        /// <summary>
        /// Retrieves a user by their unique identifier.
        /// </summary>
        Task<User?> GetByIdAsync(Guid id);

        /// <summary>
        /// Retrieves a user by their login name (case-insensitive match expected).
        /// </summary>
        Task<User?> GetByUsernameAsync(string username);

        /// <summary>
        /// Returns every user in the system.
        /// </summary>
        Task<IEnumerable<User>> GetAllAsync();

        /// <summary>
        /// Returns one deterministic, bounded projection of active Samba
        /// credential source rows. Stored NT hashes remain protected.
        /// </summary>
        Task<IReadOnlyList<SambaCredentialSource>> GetSambaCredentialBatchAsync(
            int offset,
            int count,
            CancellationToken cancellationToken);

        /// <summary>
        /// Creates a new user account.
        /// </summary>
        /// <returns>The created user with server-generated fields populated.</returns>
        Task<User> CreateAsync(User user);

        /// <summary>
        /// Persists all changes to an existing user entity.
        /// </summary>
        Task UpdateAsync(User user);

        /// <summary>
        /// Deletes a user account by its identifier.
        /// Implementations should cascade-delete related membership and assignment rows.
        /// </summary>
        Task DeleteAsync(Guid id);

        // --------------------------------------------
        //  Group / Role membership
        // --------------------------------------------

        /// <summary>Returns all groups the user belongs to.</summary>
        Task<List<Group>> GetGroupsForUserAsync(Guid userId);

        /// <summary>Returns all roles directly assigned to the user.</summary>
        Task<List<Role>> GetRolesForUserAsync(Guid userId);

        /// <summary>
        /// Replaces the user's group membership with the given set of group IDs.
        /// Groups not in <paramref name="groupIds"/> are removed; new ones are added.
        /// </summary>
        Task SetGroupsForUserAsync(Guid userId, List<Guid> groupIds);

        /// <summary>
        /// Replaces the user's role assignments with the given set of role IDs.
        /// Roles not in <paramref name="roleIds"/> are removed; new ones are added.
        /// </summary>
        Task SetRolesForUserAsync(Guid userId, List<Guid> roleIds);

        // --------------------------------------------
        //  Targeted property updates
        // --------------------------------------------

        /// <summary>
        /// Updates only the user's display name.
        /// </summary>
        Task UpdateNameAsync(Guid userId, string newName);

        /// <summary>
        /// Updates the user's password hash and NT hash.
        /// Both values must already be hashed by the caller.
        /// </summary>
        /// <param name="userId">The user to update.</param>
        /// <param name="passwordHash">The new bcrypt/argon2 password hash.</param>
        /// <param name="ntHash">The new MD4-based NT hash (required for SMB/NTLM).</param>
        Task UpdatePasswordAsync(Guid userId, string passwordHash, string ntHash);

        /// <summary>
        /// Updates the user's profile fields in a single round-trip.
        /// </summary>
        /// <param name="userId">The user to update.</param>
        /// <param name="description">Free-text description or job title.</param>
        /// <param name="email">The user's email address.</param>
        /// <param name="isEnabled">Whether the account is active.</param>
        /// <param name="canChangePassword">Whether the user is allowed to change their own password.</param>
        Task UpdateProfileAsync(Guid userId, string description, string email, bool isEnabled, bool canChangePassword);

        /// <summary>
        /// Updates the user's given/sur name (AD <c>givenName</c>/<c>sn</c>).
        /// Passing <c>null</c> clears the respective field.
        /// </summary>
        Task UpdatePersonalNamesAsync(Guid userId, string? firstName, string? lastName);

        /// <summary>
        /// Sets or clears (when <paramref name="photo"/> is <c>null</c>) the user's
        /// profile picture and its MIME type.
        /// </summary>
        Task UpdatePhotoAsync(Guid userId, byte[]? photo, string? contentType);
    }

    /// <summary>Minimal database projection used by Samba credential export.</summary>
    public sealed record SambaCredentialSource(
        string Username,
        string StoredNtHash);
}
