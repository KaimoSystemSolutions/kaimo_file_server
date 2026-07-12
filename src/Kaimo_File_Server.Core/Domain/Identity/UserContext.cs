using System;
using System.Collections.Generic;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Immutable snapshot of everything known about the currently
    /// authenticated user: identity, group memberships, roles,
    /// resolved permissions, and department affiliations.
    ///
    /// Built once per request/session and passed through the
    /// authorization pipeline so that permission checks never
    /// need additional database round-trips.
    /// </summary>
    public class UserContext
    {
        /// <summary>The authenticated user entity.</summary>
        public User User { get; init; }

        /// <summary>All groups the user is a member of.</summary>
        public HashSet<Group> Groups { get; init; }

        /// <summary>
        /// Roles the user effectively holds at GLOBAL scope — resolved from the
        /// user's own and its groups' global-scoped role assignments. Used for
        /// ACL principal matching. (Department/share-scoped roles are not here;
        /// they grant management authority, not ACL identity.)
        /// </summary>
        public HashSet<Role> Roles { get; init; }

        /// <summary>
        /// Flat set of resolved permission strings derived from
        /// <see cref="Roles"/> and any additional grants.
        /// Used for fast <c>Contains</c> lookups during authorization.
        /// </summary>
        public HashSet<string> Permissions { get; init; }

        /// <summary>
        /// Departments this user belongs to.
        /// Scoped administration checks use this set to decide whether
        /// a delegated admin may manage a given resource.
        /// </summary>
        public HashSet<Department.Department> Departments { get; init; }

        /// <param name="user">Authenticated user — must not be null.</param>
        /// <param name="groups">Group memberships.</param>
        /// <param name="roles">Assigned roles.</param>
        /// <param name="permissions">Resolved permission strings.</param>
        /// <param name="departments">Department affiliations (defaults to empty).</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="user"/>, <paramref name="groups"/>,
        /// <paramref name="roles"/>, or <paramref name="permissions"/> is null.
        /// </exception>
        public UserContext(
            User user,
            HashSet<Group> groups,
            HashSet<Role> roles,
            HashSet<string> permissions,
            HashSet<Department.Department>? departments = null)
        {
            User = user ?? throw new ArgumentNullException(nameof(user));
            Groups = groups ?? throw new ArgumentNullException(nameof(groups));
            Roles = roles ?? throw new ArgumentNullException(nameof(roles));
            Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
            Departments = departments ?? new HashSet<Department.Department>();
        }
    }
}