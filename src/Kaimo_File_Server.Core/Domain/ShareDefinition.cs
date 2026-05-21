using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain
{
    public class ShareDefinition
    {
        public Guid Id { get; set; }
        public string Name { get; set; } // Name of the folder and the share 
        public string Path { get; set; }  // e.g. "/data/storage/projekte"
        public bool IsEnabled { get; set; } // If Share is de- / activated
        public bool IsRecycleEnabled { get; set; } // If recycle bin is enable / disabled

        internal ShareDefinition() { }

        public ShareDefinition(string name, string path, bool isEnabled = true, bool isRecycleEnabled = false)
        {
            Id = Guid.NewGuid();
            Name = name;
            Path = path;
            IsEnabled = isEnabled;
            IsRecycleEnabled = isRecycleEnabled;
        }
    }
}
