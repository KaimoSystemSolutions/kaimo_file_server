using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Domain
{
    public class ShareDefinition
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }  // z.B. "/data/storage/projekte"
        public bool IsEnabled { get; set; }

        internal ShareDefinition() { }

        public ShareDefinition(string name, string path, bool isEnabled = true)
        {
            Id = Guid.NewGuid();
            Name = name;
            Path = path;
            IsEnabled = isEnabled;
        }
    }
}
