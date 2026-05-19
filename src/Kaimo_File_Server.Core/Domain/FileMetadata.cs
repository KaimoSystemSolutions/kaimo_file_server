using Kaimo_File_Server.Core.Security;

namespace Kaimo_File_Server.Core.Domain
{
    public class FileMetadata
    {
        public Guid Id { get; set; }
        public Guid ShareId { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public Guid OwnerId { get; set; }
        public IReadOnlyList<AccessEntry> Acl { get; set; } = [];
    }
}