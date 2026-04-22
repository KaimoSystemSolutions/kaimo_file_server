using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain
{
    public class ShareAccessEntry
    {
        public Guid Id { get; set; }
        public string ShareName { get; set; }
        public Guid PrincipalId { get; set; }  // User oder Gruppe

        internal ShareAccessEntry() { }

        public ShareAccessEntry(string shareName, Guid principalId)
        {
            Id = Guid.NewGuid();
            ShareName = shareName;
            PrincipalId = principalId;
        }
    }
}
