using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Services;
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
        private readonly IServiceProvider _serviceProvider;
        private readonly IFileService _fileService;
        private SMBServer? _server;

        /// <summary>
        /// Mapping: ShareName -> SmbFileSystem-Instanz.
        /// Wird beim Start befüllt und danach nur gelesen.
        /// </summary>
        private readonly Dictionary<string, SmbFileSystem> _fileSystems = new(StringComparer.OrdinalIgnoreCase);

        public SmbServer(IServiceProvider serviceProvider, IFileService fileService)
        {
            _serviceProvider = serviceProvider;
            _fileService = fileService;
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

                    // SmbFileSystem bekommt nur noch FileService — kein UserContextAccessor mehr
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
                var contextFactory = scope.ServiceProvider.GetRequiredService<IUserContextFactory>();

                var user = userRepo.GetByUsernameAsync(args.UserName).GetAwaiter().GetResult();
                if (user == null)
                {
                    args.Allow = false;
                    Console.WriteLine($"[ShareAccess] {args.UserName} -> {shareName}: user not found");
                    return;
                }

                var userContext = contextFactory.CreateAsync(user).GetAwaiter().GetResult();

                // ── Kernänderung: UserContext direkt ins FileSystem setzen ──
                // Statt über AsyncLocal wird der Context auf der SmbFileSystem-Instanz gesetzt.
                // Das ist sicherer, weil es an die konkrete Share-Verbindung gebunden ist.
                if (_fileSystems.TryGetValue(shareName, out var fileSystem))
                {
                    fileSystem.CurrentUser = userContext;
                }

                // Share-Zugriff prüfen — User-ID + Gruppen-IDs
                bool hasAccess = shareAccessRepo.HasAccessAsync(shareName, user.Id).GetAwaiter().GetResult();

                if (!hasAccess)
                {
                    foreach (var group in userContext.Groups)
                    {
                        if (shareAccessRepo.HasAccessAsync(shareName, group.Id).GetAwaiter().GetResult())
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