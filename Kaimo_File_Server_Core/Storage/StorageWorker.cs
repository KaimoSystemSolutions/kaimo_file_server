using Kaimo_File_Server_Core.Smb;
using SMBLibrary.Server;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Storage
{
    internal class StorageWorker : BackgroundService
    {
        
        private readonly SmbServer _server;

        public StorageWorker(SmbServer server)
        {
            _server = server;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _server.StartAsync(stoppingToken);
        }
        
    }
}
