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
        private readonly string _storagePath;
        private readonly object _restartLock = new();
        private SMBServer? _server;
        private FileSystemWatcher? _watcher;
        private int _debounce = 0;

        public SmbServer(IServiceProvider serviceProvider, IFileServiceFactory fileServiceFactory, string storagePath)
        {
            _serviceProvider = serviceProvider;
            _fileServiceFactory = fileServiceFactory;
            _storagePath = storagePath;
        }

        public Task StartAsync(CancellationToken token)
        {
            StartServer();
            StartWatcher();

            token.Register(() =>
            {
                lock (_restartLock)
                {
                    _watcher?.Dispose();
                    _server?.Stop();
                    Console.WriteLine("[*] SMB server stopped.");
                }
            });

            return Task.CompletedTask;
        }

        private void StartWatcher()
        {
            Directory.CreateDirectory(_storagePath);

            _watcher = new FileSystemWatcher(_storagePath)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false
            };

            _watcher.Created += (_, e) => OnStorageChanged($"Neuer Ordner: {e.Name}");
            _watcher.Deleted += (_, e) => OnStorageChanged($"Ordner gelöscht: {e.Name}");
            _watcher.Renamed += (_, e) => OnStorageChanged($"Ordner umbenannt: {e.OldName} -> {e.Name}");
            _watcher.EnableRaisingEvents = true;

            Console.WriteLine($"[+] Beobachte Storage-Ordner: {_storagePath}");
        }

        private void OnStorageChanged(string reason)
        {
            if (Interlocked.Exchange(ref _debounce, 1) == 1) return;

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                Interlocked.Exchange(ref _debounce, 0);

                Console.WriteLine($"[*] Storage-Änderung erkannt ({reason}), SMB-Server wird neu gestartet...");
                RestartServer();
            });
        }

        private SMBShareCollection BuildShareCollection()
        {
            var collection = new SMBShareCollection();

            using var scope = _serviceProvider.CreateScope();
            var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
            var shares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

            foreach (var shareDef in shares)
            {
                Directory.CreateDirectory(shareDef.Path);
                var fileService = _fileServiceFactory.CreateForShare(shareDef.Id, shareDef.Path);
                var fileSystem = new SmbFileSystem(shareDef.Path, shareDef.Name, fileService, _serviceProvider);

                var share = new FileSystemShare(shareDef.Name, fileSystem);
                share.AccessRequested += (sender, args) =>
                    OnAccessRequested(((FileSystemShare)sender).Name, args);

                collection.Add(share);
                Console.WriteLine($"[+] Share '{shareDef.Name}' ({shareDef.Id}) -> {shareDef.Path}");
            }

            return collection;
        }

        private NtHashAuthenticationProvider CreateAuthProvider()
        {
            return new NtHashAuthenticationProvider(username =>
            {
                using var scope = _serviceProvider.CreateScope();
                var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
                return authLookup.GetNtHashAsync(username).GetAwaiter().GetResult();
            });
        }

        private void StartServer()
        {
            var shares = BuildShareCollection();
            _server = new SMBServer(shares, new GSSProvider(CreateAuthProvider()));
            _server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport);
            Console.WriteLine($"[+] SMB server started mit {shares.Count} Shares");
        }

        private void RestartServer()
        {
            lock (_restartLock)
            {
                try
                {
                    _server?.Stop();
                    StartServer();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RestartServer ERROR] {ex.Message}");
                }
            }
        }

        private void OnAccessRequested(string shareName, AccessRequestArgs args)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();

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
                Console.WriteLine($"[ShareAccess ERROR] {args.UserName} -> {shareName}: {ex.Message}");
                args.Allow = false;
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _server?.Stop();
        }
    }
}