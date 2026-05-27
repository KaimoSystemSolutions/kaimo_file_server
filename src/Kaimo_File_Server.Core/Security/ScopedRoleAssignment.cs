namespace Kaimo_File_Server.Core.Security
{
    /// <summary>
    /// Defines where a role assignment applies.
    /// </summary>
    public enum ScopeType
    {
        /// <summary>System-wide, no restrictions (classic admin).</summary>
        Global = 0,

        /// <summary>Limited to a specific Department and its associated resources.</summary>
        Department = 1,

        /// <summary>Limited to a single Share.</summary>
        Share = 2
    }

    /// <summary>
    /// Links a principal (user or group) to a Role within a specific scope.
    ///
    /// This is the core of delegated administration:
    ///   "User X has Role Y within Scope Z"
    ///
    /// Examples:
    ///   - Marco → DepartmentAdmin → Department "Entwicklung"
    ///   - Anna  → ShareManager    → Share "projekte"
    ///   - admin → FullAdmin       → Global (ScopeId = Guid.Empty)
    ///
    /// The Role's ManagementPermissions define WHAT can be done.
    /// The ScopeType + ScopeId define WHERE it can be done.
    /// </summary>
    public class ScopedRoleAssignment
    {
        public Guid Id { get; set; }

        /// <summary>User or Group that receives the role.</summary>
        public Guid PrincipalId { get; set; }

        /// <summary>The role being assigned.</summary>
        public Guid RoleId { get; set; }

        /// <summary>What kind of scope this assignment is limited to.</summary>
        public ScopeType ScopeType { get; set; }

        /// <summary>
        /// The ID of the scope (Department ID, Share ID, or Guid.Empty for Global).
        /// </summary>
        public Guid ScopeId { get; set; }

        internal ScopedRoleAssignment() { }

        public ScopedRoleAssignment(Guid principalId, Guid roleId,
            ScopeType scopeType, Guid scopeId)
        {
            Id = Guid.NewGuid();
            PrincipalId = principalId;
            RoleId = roleId;
            ScopeType = scopeType;
            ScopeId = scopeId;
        }

        /// <summary>
        /// Creates a global (unscoped) role assignment.
        /// </summary>
        public static ScopedRoleAssignment Global(Guid principalId, Guid roleId)
            => new(principalId, roleId, ScopeType.Global, Guid.Empty);

        /// <summary>
        /// Creates a department-scoped role assignment.
        /// </summary>
        public static ScopedRoleAssignment ForDepartment(
            Guid principalId, Guid roleId, Guid departmentId)
            => new(principalId, roleId, ScopeType.Department, departmentId);

        /// <summary>
        /// Creates a share-scoped role assignment.
        /// </summary>
        public static ScopedRoleAssignment ForShare(
            Guid principalId, Guid roleId, Guid shareId)
            => new(principalId, roleId, ScopeType.Share, shareId);
    }
}