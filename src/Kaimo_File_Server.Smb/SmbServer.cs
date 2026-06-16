using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Smb.Security;
using Microsoft.Extensions.DependencyInjection;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Services;
using System.Collections.Concurrent;
using System.Net;

namespace Kaimo_File_Server.Smb
{
    public class SmbServer : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IFileServiceFactory _fileServiceFactory;
        private readonly string _storagePath;
        private readonly object _shareLock = new();
        private SMBServer? _server;
        private FileSystemWatcher? _watcher;
        private int _debounce = 0;

        // -- Runtime share tracking --
        // Reads happen on SMB worker threads (ABE), writes under _shareLock.
        private readonly ConcurrentDictionary<string, ShareEntry> _activeShares
            = new(StringComparer.OrdinalIgnoreCase);

        private sealed class ShareEntry
        {
            public Guid Id { get; init; }
            public string DbName { get; init; } = "";
            public string Path { get; init; } = "";
            public FileSystemShare Share { get; init; } = null!;
        }

        public SmbServer(
            IServiceProvider serviceProvider,
            IFileServiceFactory fileServiceFactory,
            string storagePath)
        {
            _serviceProvider = serviceProvider;
            _fileServiceFactory = fileServiceFactory;
            _storagePath = storagePath;
        }

        // ═══════════════════════ LIFECYCLE ═══════════════════════

        public Task StartAsync(CancellationToken token)
        {
            StartServer();
            StartWatcher();

            token.Register(() =>
            {
                lock (_shareLock)
                {
                    _watcher?.Dispose();
                    _server?.Stop();
                    Console.WriteLine("[*] SMB server gestoppt.");
                }
            });

            return Task.CompletedTask;
        }

        private void StartServer()
        {
            var shares = LoadSharesFromDb();

            _server = new SMBServer(
                shares,
                new GSSProvider(CreateAuthProvider()),
                new ShareListProvider(GetVisibleSharesForCurrentUser));

            _server.OnBeforeCommand = username =>
            {
                // Schneller Pfad: aus Cache wiederherstellen
                SmbFileSystem.RestoreSessionFromFallback(username);

                // Wenn nicht im Cache: aus DB auflösen und cachen
                if (SmbFileSystem.GetSessionUserOrDefault() == null && username != null)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
                        var userContext = authLookup.ResolveUserContextAsync(username)
                            .GetAwaiter().GetResult();
                        if (userContext != null)
                            SmbFileSystem.SetSessionUser(userContext);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OnBeforeCommand] Failed to resolve '{username}': {ex.Message}");
                    }
                }
            };

            _server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport);
            Console.WriteLine($"[+] SMB server gestartet mit {shares.Count} Shares (ABE aktiv)");
        }

        private SMBShareCollection LoadSharesFromDb()
        {
            var collection = new SMBShareCollection();

            using var scope = _serviceProvider.CreateScope();
            var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
            var dbShares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

            foreach (var shareDef in dbShares)
            {
                var fsShare = CreateFileSystemShare(shareDef.Id, shareDef.Name, shareDef.Path);
                collection.Add(fsShare);

                _activeShares[shareDef.Name] = new ShareEntry
                {
                    Id = shareDef.Id,
                    DbName = shareDef.Name,
                    Path = shareDef.Path,
                    Share = fsShare
                };

                Console.WriteLine($"[+] Share '{shareDef.Name}' ({shareDef.Id}) -> {shareDef.Path}");
            }

            return collection;
        }

        // ═══════════════════════ FILE SYSTEM WATCHER ═══════════════════════

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

                Console.WriteLine($"[*] Storage-Änderung erkannt ({reason}), Shares werden synchronisiert...");
                SyncFromDb();
            });
        }

        // ═══════════════════════ DYNAMIC SHARE MANAGEMENT ═══════════════════════
        //
        // All mutations go through _shareLock so operations complete atomically.
        // Call these from API controllers, background services, or SignalR hubs.
        // No server restart — existing SMB connections stay alive.

        /// <summary>
        /// Adds a new share at runtime. Immediately accessible for new tree connects.
        /// </summary>
        public void AddShare(Guid shareId, string name, string path)
        {
            lock (_shareLock)
            {
                if (_server == null)
                    throw new InvalidOperationException("SMB server is not running.");

                if (_activeShares.ContainsKey(name))
                {
                    Console.WriteLine($"[~] Share '{name}' existiert bereits, übersprungen.");
                    return;
                }

                var fsShare = CreateFileSystemShare(shareId, name, path);
                _server.AddShare(fsShare);

                _activeShares[name] = new ShareEntry
                {
                    Id = shareId,
                    DbName = name,
                    Path = path,
                    Share = fsShare
                };

                Console.WriteLine($"[+] Share '{name}' ({shareId}) -> {path} [live hinzugefügt]");
            }
        }

        /// <summary>
        /// Removes a share at runtime. Active tree connections remain functional
        /// until the client disconnects — only new tree connects are blocked.
        /// </summary>
        public bool RemoveShare(string name)
        {
            lock (_shareLock)
            {
                if (_server == null)
                    return false;

                if (!_activeShares.TryRemove(name, out var entry))
                {
                    Console.WriteLine($"[~] Share '{name}' nicht gefunden, übersprungen.");
                    return false;
                }

                _server.RemoveShare(name);
                Console.WriteLine($"[-] Share '{name}' ({entry.Id}) [live entfernt]");
                return true;
            }
        }

        /// <summary>
        /// Updates an existing share (path changed, renamed, etc.).
        /// Internally removes the old and adds the new share.
        /// Active connections to the old share remain until the client disconnects.
        /// </summary>
        public void UpdateShare(string oldName, Guid shareId, string newName, string newPath)
        {
            lock (_shareLock)
            {
                if (_server == null)
                    throw new InvalidOperationException("SMB server is not running.");

                if (_activeShares.TryRemove(oldName, out _))
                    _server.RemoveShare(oldName);

                var fsShare = CreateFileSystemShare(shareId, newName, newPath);
                _server.AddShare(fsShare);

                _activeShares[newName] = new ShareEntry
                {
                    Id = shareId,
                    DbName = newName,
                    Path = newPath,
                    Share = fsShare
                };

                Console.WriteLine($"[~] Share aktualisiert: '{oldName}' -> '{newName}' ({shareId}) -> {newPath}");
            }
        }

        /// <summary>
        /// Full resync with the database. Adds new shares, removes deleted ones,
        /// updates changed ones. No server restart, no connection interruption.
        /// All changes happen atomically under a single lock.
        /// </summary>
        public void SyncFromDb()
        {
            lock (_shareLock)
            {
                if (_server == null)
                    return;

                using var scope = _serviceProvider.CreateScope();
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var dbShares = shareRepo.GetAllEnabledAsync().GetAwaiter().GetResult();

                var dbByName = new Dictionary<string, (Guid Id, string Name, string Path)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var s in dbShares)
                    dbByName[s.Name] = (s.Id, s.Name, s.Path);

                // Remove shares no longer in DB
                foreach (var active in _activeShares.Values.ToList())
                {
                    if (!dbByName.ContainsKey(active.DbName))
                    {
                        _activeShares.TryRemove(active.DbName, out _);
                        _server.RemoveShare(active.DbName);
                        Console.WriteLine($"[-] Share '{active.DbName}' ({active.Id}) [sync entfernt]");
                    }
                }

                // Add new or update changed shares
                foreach (var (id, name, path) in dbByName.Values)
                {
                    if (_activeShares.TryGetValue(name, out var existing))
                    {
                        // Path changed → replace
                        if (!string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
                        {
                            _activeShares.TryRemove(name, out _);
                            _server.RemoveShare(name);

                            var fsShare = CreateFileSystemShare(id, name, path);
                            _server.AddShare(fsShare);

                            _activeShares[name] = new ShareEntry
                            {
                                Id = id,
                                DbName = name,
                                Path = path,
                                Share = fsShare
                            };

                            Console.WriteLine($"[~] Share '{name}' Pfad aktualisiert -> {path}");
                        }
                    }
                    else
                    {
                        // New share
                        var fsShare = CreateFileSystemShare(id, name, path);
                        _server.AddShare(fsShare);

                        _activeShares[name] = new ShareEntry
                        {
                            Id = id,
                            DbName = name,
                            Path = path,
                            Share = fsShare
                        };

                        Console.WriteLine($"[+] Share '{name}' ({id}) -> {path} [sync hinzugefügt]");
                    }
                }

                Console.WriteLine($"[*] DB-Sync abgeschlossen: {_activeShares.Count} aktive Shares");
            }
        }

        // ═══════════════════════ SHARE FACTORY ═══════════════════════

        private FileSystemShare CreateFileSystemShare(Guid shareId, string name, string path)
        {
            Directory.CreateDirectory(path);

            var fileService = _fileServiceFactory.CreateForShare(shareId, path);
            var fileSystem = new SmbFileSystem(path, name, fileService, _serviceProvider);

            var share = new FileSystemShare(name, fileSystem);
            share.AccessRequested += (sender, args) =>
                OnAccessRequested(shareId, args);

            return share;
        }

        // ═══════════════════════ AUTH ═══════════════════════

        private NtHashAuthenticationProvider CreateAuthProvider()
        {
            return new NtHashAuthenticationProvider(username =>
            {
                using var scope = _serviceProvider.CreateScope();
                var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
                return authLookup.GetNtHashAsync(username).GetAwaiter().GetResult();
            });
        }

        private void OnAccessRequested(Guid shareId, AccessRequestArgs args)
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

                args.Allow = authLookup
                    .CanListShareAsync(shareId, userContext.User.Id)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShareAccess ERROR] {args.UserName} -> {shareId}: {ex.Message}");
                args.Allow = false;
            }
        }

        // ═══════════════════════ ABE ═══════════════════════

        /// <summary>
        /// Called by SMBLibrary (via ShareListProvider delegate) on every
        /// NetrShareEnum / NetrShareGetInfo RPC request.
        /// Returns only the share names the current session user has access to.
        /// </summary>
        private List<string> GetVisibleSharesForCurrentUser()
        {
            try
            {
                var userContext = SmbFileSystem.GetSessionUserOrDefault();
                if (userContext == null)
                    return new List<string>();

                using var scope = _serviceProvider.CreateScope();
                var authLookup = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();

                var visible = new List<string>();
                foreach (var entry in _activeShares.Values)
                {
                    try
                    {
                        var hasAccess = authLookup
                            .CanListShareAsync(entry.Id, userContext.User.Id)
                            .GetAwaiter().GetResult();

                        if (hasAccess)
                            visible.Add(entry.DbName);
                    }
                    catch
                    {
                        // Can't determine access → hide (safe default)
                    }
                }

                return visible;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ABE ERROR] {ex.Message}");
                return new List<string>();
            }
        }

        // ═══════════════════════ DISPOSE ═══════════════════════

        public void Dispose()
        {
            _watcher?.Dispose();
            _server?.Stop();
        }
    }
}