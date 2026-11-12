using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security.Management;

namespace Kaimo_File_Server.Core.Services.Management;

/// <summary>
/// What a user loses by leaving a department: the memberships in the department's groups and
/// the roles assigned to the user directly at that department. The role is <c>null</c> when the
/// assignment points to a role that no longer exists.
/// </summary>
public sealed record DepartmentLeaveImpact(
    IReadOnlyList<Group> Groups,
    IReadOnlyList<(ScopedRoleAssignment Assignment, Role? Role)> Roles)
{
    public bool IsEmpty => Groups.Count == 0 && Roles.Count == 0;
}

/// <summary>
/// Describes the resolved default permission and its source in the hierarchy.
/// </summary>
public class DepartmentPermissionInfo
{
    /// <summary>The effective permission value (combined flags).</summary>
    public long EffectivePermission { get; init; }

    /// <summary>True if the permission is defined directly on this department.</summary>
    public bool IsOwnPermission { get; init; }

    /// <summary>
    /// If inherited, the ID of the ancestor that defines the permission.
    /// Null if the permission is defined on this department or if no default is set.
    /// </summary>
    public Guid? InheritedFromDepartmentId { get; init; }

    /// <summary>
    /// If inherited, the name of the ancestor department.
    /// Null if the permission is defined on this department or if no default is set.
    /// </summary>
    public string? InheritedFromDepartmentName { get; init; }

    /// <summary>True if no department in the chain defines a default permission.</summary>
    public bool HasNoDefault => EffectivePermission == 0
                               && !IsOwnPermission
                               && InheritedFromDepartmentId == null;
}
