using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Client;
using SMBLibrary.Server;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Kaimo_File_Server_Core.Smb
{
    public class SmbServer
    {
        private readonly INTFileStore _fileStore;
        private SMBServer _server;

        public SmbServer(INTFileStore fileStore)
        {
            _fileStore = fileStore;
            
        }

        public Task StartAsync(CancellationToken token)
        {
            FileSystemShare share = new("test", _fileStore);
            SMBShareCollection shareCollection = new SMBShareCollection();
            shareCollection.Add(share);

            // Eingebauter NTLM Provider statt eigener GSSMechanism
            NTLMAuthenticationProviderBase authProvider = new IndependentNTLMAuthenticationProvider(
                password => ""  // alle Passwörter akzeptieren = Guest
            );

            GSSProvider gssProvider = new GSSProvider(authProvider);

            _server = new SMBServer(shareCollection, gssProvider);
            _server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport);
            Console.WriteLine("SMB server started.");

            return Task.CompletedTask;
        }
    }
}
