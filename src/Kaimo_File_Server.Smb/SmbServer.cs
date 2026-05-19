using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Smb.Security;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Kaimo_File_Server.Smb
{
    public class SmbServer : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IFileServiceFactory _fileServiceFactory;
        private SMBServer? _server;

        public SmbServer(IServiceProvider serviceProvider, IFileServiceFactory fileServiceFactory)
        {
            _serviceProvider = serviceProvider;
            _fileServiceFactory = fileServiceFactory;
        }

        public Task StartAsync(CancellationToken token)
        {
            var shareCollection = new SMBShareCollection();

            using (var scope = _serviceProvider.CreateScope())
            {
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var shares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

                foreach (var shareDef in shares)
                {
                    Directory.CreateDirectory(shareDef.Path);
                    var fileService = _fileServiceFactory.CreateForShare(shareDef.Id, shareDef.Path);
                    var fileSystem = new SmbFileSystem(shareDef.Path, shareDef.Name, fileService, _serviceProvider);

                    var share = new FileSystemShare(shareDef.Name, fileSystem);
                    share.AccessRequested += (sender, args) =>
                    {
                        var shareName = ((FileSystemShare)sender).Name;
                        OnAccessRequested(shareName, args);
                    };

                    shareCollection.Add(share);
                    Console.WriteLine($"[+] Share '{shareDef.Name}' ({shareDef.Id}) -> {shareDef.Path}");
                }
            }

            NTLMAuthenticationProviderBase authProvider = new NtHashAuthenticationProvider(
                username =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var authLookup = scope.ServiceProvider
                        .GetRequiredService<IAuthenticationLookup>();
                    return authLookup.GetNtHashAsync(username).GetAwaiter().GetResult();
                }
            );

            var gssProvider = new GSSProvider(authProvider);

            _server = new SMBServer(shareCollection, gssProvider);
            _server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport);
            Console.WriteLine($"[+] SMB server started mit {shareCollection.Count} Shares");

            token.Register(() =>
            {
                Console.WriteLine("[*] SMB server stopping...");
                _server.Stop();
                Console.WriteLine("[*] SMB server stopped.");
            });

            return Task.CompletedTask;
        }

        private void OnAccessRequested(string shareName, AccessRequestArgs args)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var authLookup = scope.ServiceProvider
                    .GetRequiredService<IAuthenticationLookup>();

                var userContext = authLookup.ResolveUserContextAsync(args.UserName)
                    .GetAwaiter().GetResult();
                if (userContext == null)
                {
                    args.Allow = false;
                    return;
                }

                SmbFileSystem.SetSessionUser(userContext);

                bool hasAccess = authLookup
                    .HasShareAccessAsync(shareName, userContext.User.Id)
                    .GetAwaiter().GetResult();

                if (!hasAccess)
                {
                    foreach (var group in userContext.Groups)
                    {
                        if (authLookup.HasShareAccessAsync(shareName, group.Id)
                                .GetAwaiter().GetResult())
                        {
                            hasAccess = true;
                            break;
                        }
                    }
                }

                args.Allow = hasAccess;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ShareAccess ERROR] {args.UserName} -> {shareName}: {ex.Message}");
                args.Allow = false;
            }
        }

        public void Dispose()
        {
            _server?.Stop();
        }
    }
}