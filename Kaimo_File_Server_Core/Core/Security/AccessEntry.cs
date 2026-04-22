using static Kaimo_File_Server_Core.Core.Security.FilePermission;

namespace Kaimo_File_Server_Core.Core.Security
{
    public class AccessEntry
    {
        public Guid Id { get; set; }
        public Guid FileMetadataId { get; set; }
        public Guid PrincipalId { get; set; }          // User oder Gruppe
        public AclEntryType EntryType { get; set; }     // Allow oder Deny
        public FilePermission Permissions { get; set; }
        public AclInheritance Inheritance { get; set; }

        internal AccessEntry() { }

        public AccessEntry(Guid principalId, AclEntryType entryType,
            FilePermission permissions, AclInheritance inheritance)
        {
            Id = Guid.NewGuid();
            PrincipalId = principalId;
            EntryType = entryType;
            Permissions = permissions;
            Inheritance = inheritance;
        }
    }
}
