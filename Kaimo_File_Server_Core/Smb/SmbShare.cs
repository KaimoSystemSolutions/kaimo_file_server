using System;
using System.Collections.Generic;
using System.Text;
using SMBLibrary;
using SMBLibrary.Server;

namespace Kaimo_File_Server_Core.Smb
{
    public class SmbShare : ISMBShare
    {
        public string Name { get; }

        public INTFileStore FileStore { get; }

        public SmbShare(string name, INTFileStore fileStore)
        {
            Name = name;
            FileStore = fileStore;
        }

        public bool HasAccess(SecurityContext context, AccessMask desiredAccess)
        {
            // erstmal alles erlauben
            return true;
        }
    }
}
