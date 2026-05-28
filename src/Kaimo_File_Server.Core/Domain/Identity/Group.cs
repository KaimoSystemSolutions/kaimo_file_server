using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// A named collection of users.
    /// Groups are referenced in ACLs (<see cref="Security.AccessEntry"/>)
    /// and share-level access (<see cref="ShareAccessEntry"/>) to grant
    /// permissions to multiple users at once.
    /// </summary>
    public class Group : Identity
    {
        /// <summary>EF Core / serialization constructor.</summary>
        protected Group() { }

        /// <param name="id">Unique identifier for this group.</param>
        /// <param name="name">Display name (e.g. "Developers", "HR-Team").</param>
        public Group(Guid id, string name) : base(id, name) { }
    }
}