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
        Task<List<ScopedRoleAssignment>> GetByPrincipalAsync(Guid principalId);

        /// <summary>
        /// Returns all role assignments that reference a specific role.
        /// Used to display "who has this role?" without N+1 queries.
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetByRoleAsync(Guid roleId);

        /// <summary>
        /// Returns all role assignments within a specific scope.
        /// </summary>
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
        Task<List<ScopedRoleAssignment>> GetEffectiveAssignmentsAsync(
            Guid userId, IEnumerable<Guid> groupIds);

        /// <summary>
        /// Creates a new scoped role assignment.
        /// </summary>
        Task<ScopedRoleAssignment> CreateAsync(ScopedRoleAssignment assignment);

        /// <summary>
        /// Deletes a single role assignment.
        /// </summary>
        Task DeleteAsync(Guid id);

        /// <summary>
        /// Removes all role assignments that belong to a given scope.
        /// </summary>
        Task DeleteByScopeAsync(ScopeType scopeType, Guid scopeId);
    }
}