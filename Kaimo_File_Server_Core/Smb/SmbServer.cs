using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Infrastructure.Repositories;
using Kaimo_File_Server_Core.Smb.Security;
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
        private readonly FileService _fileService;
        private readonly IServiceProvider _serviceProvider;
        private readonly UserContextAccessor _userContextAccessor;
        private SMBServer _server;

        public SmbServer(IServiceProvider serviceProvider, FileService fileService, UserContextAccessor userContextAccessor)
        {
            _serviceProvider = serviceProvider;
            _fileService = fileService;
            _userContextAccessor = userContextAccessor;
        }

        public Task StartAsync(CancellationToken token)
        {
            SMBShareCollection shareCollection = new SMBShareCollection();

            // Shares aus DB laden
            using (var scope = _serviceProvider.CreateScope())
            {
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var shares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

                foreach (var shareDef in shares)
                {
                    // Verzeichnis erstellen falls nicht vorhanden
                    Directory.CreateDirectory(shareDef.Path);

                    var fileSystem = new SmbFileSystem(shareDef.Path, _fileService, _userContextAccessor);
                    var share = new FileSystemShare(shareDef.Name, fileSystem);

                    // ACL-Prüfung pro Share
                    share.AccessRequested += (sender, args) =>
                    {
                        var shareName = ((FileSystemShare)sender).Name;
                        OnAccessRequested(shareName, args);
                    };

                    shareCollection.Add(share);
                    Console.WriteLine($"[+] Share '{shareDef.Name}' → {shareDef.Path}");
                }
            }

            NTLMAuthenticationProviderBase authProvider = new NtHashAuthenticationProvider(
                username =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                    var user = userRepo.GetByUsernameAsync(username).GetAwaiter().GetResult();
                    if (user == null) return null;
                    return Convert.FromHexString(user.NtHash);
                }
            );

            GSSProvider gssProvider = new GSSProvider(authProvider);

            _server = new SMBServer(shareCollection, gssProvider);
            _server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport);
            Console.WriteLine($"[+] SMB server started mit {shareCollection.Count} Shares");

            return Task.CompletedTask;
        }

        private void OnAccessRequested(string shareName, AccessRequestArgs args)
        {
            using var scope = _serviceProvider.CreateScope();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var shareAccessRepo = scope.ServiceProvider.GetRequiredService<IShareAccessRepository>();

            var user = userRepo.GetByUsernameAsync(args.UserName).GetAwaiter().GetResult();
            if (user == null)
            {
                args.Allow = false;
                return;
            }

            args.Allow = shareAccessRepo.HasAccessAsync(shareName, user.Id).GetAwaiter().GetResult();
            Console.WriteLine($"[ShareAccess] {args.UserName} → {shareName}: {(args.Allow ? "✓" : "✗")}");
        }
    }
}
