using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Repositories
{
    public interface IScopedRoleAssignmentRepository
    {
        Task<ScopedRoleAssignment?> GetByIdAsync(Guid id);

        /// <summary>
        /// Get all role assignments for a principal (user or group).
        /// Includes Global, Department, and Share scoped assignments.
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetByPrincipalAsync(Guid principalId);

        /// <summary>
        /// Get all role assignments for a specific scope.
        /// E.g. "who has roles in Department X?"
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetByScopeAsync(ScopeType scopeType, Guid scopeId);

        /// <summary>
        /// Get all role assignments for a principal within a specific scope.
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetByPrincipalAndScopeAsync(
            Guid principalId, ScopeType scopeType, Guid scopeId);

        /// <summary>
        /// Get all assignments that grant a specific principal ANY role
        /// (used to collect all effective permissions across all scopes).
        /// Includes assignments via group membership.
        /// </summary>
        Task<List<ScopedRoleAssignment>> GetEffectiveAssignmentsAsync(
            Guid userId, IEnumerable<Guid> groupIds);

        Task<ScopedRoleAssignment> CreateAsync(ScopedRoleAssignment assignment);
        Task DeleteAsync(Guid id);

        /// <summary>
        /// Remove all assignments for a scope (e.g. when deleting a department).
        /// </summary>
        Task DeleteByScopeAsync(ScopeType scopeType, Guid scopeId);
    }
}