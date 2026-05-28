using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Repositories
{
    /// <summary>
    /// Repository for scoped role assignments that bind a principal (user or group)
    /// to a role within a specific scope (Global, Department, or Share).
    /// </summary>
    public interface IScopedRoleAssignmentRepository
    {
        /// <summary>
        /// Retrieves a single role assignment by its unique identifier.
        /// </summary>
        Task<ScopedRoleAssignment?> GetByIdAsync(Guid id);

        /// <summary>
        /// Returns all role assignments for a given principal (user or group),
        /// across every scope type.
        /// </summary>
        /// <param name="principalId">The user or group identifier.</param>
        Task<List<ScopedRoleAssignment>> GetByPrincipalAsync(Guid principalId);

        /// <summary>
        /// Returns all role assignments within a specific scope.
        /// Example: "Who has roles in Department X?"
        /// </summary>
        /// <param name="scopeType">The kind of scope (Global, Department, Share).</param>
        /// <param name="scopeId">The identifier of the scope entity.</param>
        Task<List<ScopedRoleAssignment>> GetByScopeAsync(ScopeType scopeType, Guid scopeId);

        /// <summary>
        /// Returns all role assignments for a principal within a single scope.
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetByPrincipalAndScopeAsync(
            Guid principalId, ScopeType scopeType, Guid scopeId);

        /// <summary>
        /// Collects the effective role assignments for a user by considering
        /// both direct user assignments and assignments inherited via group membership.
        /// </summary>
        /// <param name="userId">The user's identifier.</param>
        /// <param name="groupIds">
        /// All group IDs the user belongs to (pre-resolved by the caller).
        /// </param>
        Task<List<ScopedRoleAssignment>> GetEffectiveAssignmentsAsync(
            Guid userId, IEnumerable<Guid> groupIds);

        /// <summary>
        /// Creates a new scoped role assignment.
        /// </summary>
        /// <returns>The created assignment with server-generated fields populated.</returns>
        Task<ScopedRoleAssignment> CreateAsync(ScopedRoleAssignment assignment);

        /// <summary>
        /// Deletes a single role assignment.
        /// </summary>
        Task DeleteAsync(Guid id);

        /// <summary>
        /// Removes all role assignments that belong to a given scope.
        /// Typically called when deleting the scope entity itself (e.g. a department).
        /// </summary>
        Task DeleteByScopeAsync(ScopeType scopeType, Guid scopeId);
    }
}