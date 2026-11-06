using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Services;

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
