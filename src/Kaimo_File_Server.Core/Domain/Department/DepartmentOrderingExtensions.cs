using System;
using System.Collections.Generic;
using System.Linq;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Domain.Department
{
    /// <summary>
    /// Ordering helpers that keep the well-known Global department pinned to the
    /// top of any department list surfaced to the user (dropdowns, pickers), so
    /// that <see cref="WellKnownGUIDs.DEPARTMENT_GLOBAL"/> is always the first
    /// option regardless of its name's alphabetical position.
    /// </summary>
    public static class DepartmentOrderingExtensions
    {
        /// <summary>
        /// Orders departments so the Global department
        /// (<see cref="WellKnownGUIDs.DEPARTMENT_GLOBAL"/>) always comes first,
        /// followed by the remaining departments sorted by name
        /// (culture-aware, case-insensitive).
        /// </summary>
        public static IOrderedEnumerable<Department> OrderByGlobalFirst(
            this IEnumerable<Department> departments)
            => departments
                .OrderBy(department => department.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL ? 0 : 1)
                .ThenBy(department => department.Name, StringComparer.CurrentCultureIgnoreCase);
    }
}
