using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Security
{
    /// <summary>
    /// Result of a scoped authorization query like
    /// "Which departments/shares may this actor manage?"
    ///
    /// Replaces the old pattern where an empty list for Global Admins
    /// meant "all" — which was indistinguishable from "none".
    ///
    /// Usage:
    ///   var result = await mgmtAuth.GetAuthorizedDepartmentIdsAsync(actor, perm);
    ///   if (result.IsUnrestricted)  → actor may manage ALL entities of this type
    ///   else                        → actor may only manage result.ScopeIds
    /// </summary>
    public sealed class AuthorizedScopeResult
    {
        /// <summary>
        /// When <c>true</c>, the actor has the requested permission globally
        /// and is not limited to any specific set of IDs.
        /// Callers should treat this as "all entities" without filtering.
        /// </summary>
        public bool IsUnrestricted { get; }

        /// <summary>
        /// The specific entity IDs the actor may manage.
        /// Only meaningful when <see cref="IsUnrestricted"/> is <c>false</c>.
        /// Empty + not unrestricted = no permission at all.
        /// </summary>
        public IReadOnlyList<Guid> ScopeIds { get; }

        private AuthorizedScopeResult(bool isUnrestricted, IReadOnlyList<Guid> scopeIds)
        {
            IsUnrestricted = isUnrestricted;
            ScopeIds = scopeIds;
        }

        /// <summary>Actor has global access — no filtering needed.</summary>
        public static AuthorizedScopeResult Unrestricted()
            => new(true, Array.Empty<Guid>());

        /// <summary>Actor is limited to specific scope IDs (may be empty = no access).</summary>
        public static AuthorizedScopeResult LimitedTo(IEnumerable<Guid> ids)
            => new(false, ids.Distinct().ToList().AsReadOnly());

        /// <summary>Actor has no access at all.</summary>
        public static AuthorizedScopeResult None()
            => new(false, Array.Empty<Guid>());
    }
}
