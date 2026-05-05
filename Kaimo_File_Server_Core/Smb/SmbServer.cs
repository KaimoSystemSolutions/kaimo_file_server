using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Infrastructure.Repositories;
using Kaimo_File_Server_Core.Smb.Security;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using System.Net;

namespace Kaimo_File_Server_Core.Smb
{
    public class SmbServer : IDisposable
    {
        private readonly FileService _fileService;
        private readonly IServiceProvider _serviceProvider;
        private readonly UserContextAccessor _userContextAccessor;
        private SMBServer? _server;

        public SmbServer(IServiceProvider serviceProvider, FileService fileService, UserContextAccessor userContextAccessor)
        {
            _serviceProvider = serviceProvider;
            _fileService = fileService;
            _userContextAccessor = userContextAccessor;
        }

        public Task StartAsync(CancellationToken token)
        {
            var shareCollection = new SMBShareCollection();

            // Shares aus DB laden
            using (var scope = _serviceProvider.CreateScope())
            {
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var shares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

                foreach (var shareDef in shares)
                {
                    Directory.CreateDirectory(shareDef.Path);

                    var fileSystem = new SmbFileSystem(shareDef.Path, _fileService, _userContextAccessor);
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
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var shareAccessRepo = scope.ServiceProvider.GetRequiredService<IShareAccessRepository>();

                var user = userRepo.GetByUsernameAsync(args.UserName).GetAwaiter().GetResult();
                if (user == null)
                {
                    args.Allow = false;
                    Console.WriteLine($"[ShareAccess] {args.UserName} -> {shareName}: user not found");
                    return;
                }

                // UserContext setzen, damit FileService spaeter weiss wer zugreift
                _userContextAccessor.Set(new UserContext(
                    user,
                    new HashSet<Group>(),
                    new HashSet<Role>(),
                    new HashSet<string>()
                ));

                args.Allow = shareAccessRepo.HasAccessAsync(shareName, user.Id).GetAwaiter().GetResult();
                Console.WriteLine($"[ShareAccess] {args.UserName} -> {shareName}: {(args.Allow ? "allowed" : "denied")}");
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