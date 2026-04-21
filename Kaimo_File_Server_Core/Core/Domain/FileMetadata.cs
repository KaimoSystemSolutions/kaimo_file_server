using Kaimo_File_Server_Core.Core.Security;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain
{
    public class FileMetadata
    {
        public string Path { get; init; }
        public string Name { get; init; }

        public long Size { get; init; }
        public bool IsDirectory { get; init; }

        public DateTime CreatedAt { get; init; }
        public DateTime ModifiedAt { get; init; }

        public string OwnerId { get; init; }

        public IReadOnlyList<AccessEntry> Acl { get; init; }
    }
}
