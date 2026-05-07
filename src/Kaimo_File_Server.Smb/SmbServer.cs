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
        private readonly IFileService _fileService;
        private SMBServer? _server;

        private readonly Dictionary<string, SmbFileSystem> _fileSystems = new(StringComparer.OrdinalIgnoreCase);

        public SmbServer(IServiceProvider serviceProvider, IFileService fileService)
        {
            _serviceProvider = serviceProvider;
            _fileService = fileService;
        }

        public Task StartAsync(CancellationToken token)
        {
            var shareCollection = new SMBShareCollection();

            // Shares laden — einmalig beim Start über IShareRepository
            using (var scope = _serviceProvider.CreateScope())
            {
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var shares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

                foreach (var shareDef in shares)
                {
                    Directory.CreateDirectory(shareDef.Path);

                    var fileSystem = new SmbFileSystem(shareDef.Path, _fileService);
                    _fileSystems[shareDef.Name] = fileSystem;

                    var share = new FileSystemShare(shareDef.Name, fileSystem);
                    share.AccessRequested += (sender, args) =>
                    {
                        var shareName = ((FileSystemShare)sender).Name;
                        OnAccessRequested(shareName, args);
                    };

                    shareCollection.Add(share);
                    Console.WriteLine($"[+] Share '{shareDef.Name}' -> {shareDef.Path}");
                }
            }

            // Auth-Provider nutzt IAuthenticationLookup statt direkt IUserRepository
            NTLMAuthenticationProviderBase authProvider = new NtHashAuthenticationProvider(
                username =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
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
                var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();

                var userContext = authLookup.ResolveUserContextAsync(args.UserName).GetAwaiter().GetResult();
                if (userContext == null)
                {
                    args.Allow = false;
                    Console.WriteLine($"[ShareAccess] {args.UserName} -> {shareName}: user not found");
                    return;
                }

                if (_fileSystems.TryGetValue(shareName, out var fileSystem))
                    fileSystem.CurrentUser = userContext;

                // Share-Zugriff prüfen — User-ID + Gruppen-IDs
                bool hasAccess = authLookup.HasShareAccessAsync(shareName, userContext.User.Id)
                    .GetAwaiter().GetResult();

                if (!hasAccess)
                {
                    foreach (var group in userContext.Groups)
                    {
                        if (authLookup.HasShareAccessAsync(shareName, group.Id).GetAwaiter().GetResult())
                        {
                            hasAccess = true;
                            break;
                        }
                    }
                }

                args.Allow = hasAccess;
                Console.WriteLine($"[ShareAccess] {args.UserName} -> {shareName}: {(hasAccess ? "allowed" : "denied")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShareAccess ERROR] {args.UserName} -> {shareName}: {ex.Message}");
                args.Allow = false;
            }
        }

        public void Dispose()
        {
            _server?.Stop();
        }
    }
}
