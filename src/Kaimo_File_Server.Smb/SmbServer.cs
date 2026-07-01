using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.DependencyInjection;
using Smb.Auth.Ntlm;
using System.Collections.Concurrent;
using System.Net;
using LibSmbServer = Smb.Host.SmbServer;
using SmbServerBuilder = Smb.Host.SmbServerBuilder;

namespace Kaimo_File_Server.Smb
{
    /// <summary>
    /// Hosts the self-written SMB-2/3 server (<see cref="Smb.Host.SmbServer"/>) for the Kaimo file
    /// server. Loads shares from the database, watches the storage directory, and reconciles shares at
    /// runtime — without a server restart. Authentication, per-user/per-path ACLs, ABE and snapshots are
    /// provided through the Kaimo bridge types (<see cref="KaimoIdentityBackend"/>,
    /// <see cref="KaimoSharePolicy"/>, <see cref="KaimoFileStore"/>).
    /// </summary>
    public class SmbServer : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IFileServiceFactory _fileServiceFactory;
        private readonly string _storagePath;
        private readonly object _shareLock = new();

        private LibSmbServer? _server;
        private FileSystemWatcher? _watcher;
        private int _debounce = 0;

        // -- Runtime share tracking (name → entry), used to diff against the DB in SyncFromDb. --
        private readonly ConcurrentDictionary<string, ShareEntry> _activeShares
            = new(StringComparer.OrdinalIgnoreCase);

        private sealed class ShareEntry
        {
            public Guid Id { get; init; }
            public string DbName { get; init; } = "";
            public string Path { get; init; } = "";
            public bool IsHidden { get; init; }
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

        /// <summary>True while the underlying SMB server is up and accepting connections.</summary>
        public bool IsRunning
        {
            get { lock (_shareLock) { return _server != null; } }
        }

        /// <summary>
        /// Back-compat entry point: starts the server and tears it down when the token is cancelled.
        /// New callers should prefer <see cref="Start"/> / <see cref="Stop"/> directly.
        /// </summary>
        public Task StartAsync(CancellationToken token)
        {
            Start();
            token.Register(() => Stop());
            return Task.CompletedTask;
        }

        /// <summary>
        /// Starts the SMB server and the storage watcher. Idempotent. A fresh server instance is
        /// created on every start so it can be stopped and started again.
        /// </summary>
        public void Start()
        {
            lock (_shareLock)
            {
                if (_server != null)
                {
                    Console.WriteLine("[~] SMB server läuft bereits, Start übersprungen.");
                    return;
                }

                StartServer();
                StartWatcher();
            }
        }

        /// <summary>
        /// Stops the SMB server and the storage watcher. Idempotent. Active connections are dropped.
        /// After a stop the server can be restarted via <see cref="Start"/>.
        /// </summary>
        public void Stop()
        {
            lock (_shareLock)
            {
                if (_server == null)
                {
                    Console.WriteLine("[~] SMB server läuft nicht, Stop übersprungen.");
                    return;
                }

                _watcher?.Dispose();
                _watcher = null;

                SmbSync.Run(() => _server.DisposeAsync()); // StopAsync + dispose
                _server = null;
                _activeShares.Clear();

                Console.WriteLine("[*] SMB server gestoppt.");
            }
        }

        private void StartServer()
        {
            // Wire the user registry to DI so cached SMB identities expire and get re-resolved
            // (picking up revoked permissions / disabled accounts) instead of living forever.
            KaimoUserRegistry.Initialize(_serviceProvider);

            var backend = new KaimoIdentityBackend(_serviceProvider);
            var policy = new KaimoSharePolicy(_serviceProvider);
            var ntlmOptions = new NtlmServerOptions
            {
                NetbiosDomainName = "WORKGROUP",
                NetbiosComputerName = Environment.MachineName.ToUpperInvariant(),
            };

            SmbServerBuilder builder = SmbServerBuilder.Create()
                .WithEndpoint(IPAddress.Any, 445)
                .WithServerName(Environment.MachineName)
                .UseAuthentication(new NtlmSpnegoNegotiator(backend, ntlmOptions))
                .UseShareAuthorization(policy)
                .WithLogger(msg => Console.WriteLine($"[smb] {msg}"));

            foreach (KaimoShare share in LoadSharesFromDb())
                builder.AddShare(share);

            _server = builder.Build();
            try
            {
                SmbSync.Run(() => _server.StartAsync());
            }
            catch
            {
                SmbSync.Run(() => _server.DisposeAsync());
                _server = null;
                throw;
            }

            Console.WriteLine($"[+] SMB server gestartet mit {_activeShares.Count} Shares (ABE aktiv)");
        }

        private List<KaimoShare> LoadSharesFromDb()
        {
            var shares = new List<KaimoShare>();

            using var scope = _serviceProvider.CreateScope();
            var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
            var dbShares = SmbSync.Run(() => shareRepo.GetAllEnabledAsync());

            foreach (var def in dbShares)
            {
                shares.Add(CreateShare(def.Id, def.Name, def.Path, def.IsShareHidden));
                _activeShares[def.Name] = new ShareEntry
                {
                    Id = def.Id,
                    DbName = def.Name,
                    Path = def.Path,
                    IsHidden = def.IsShareHidden,
                };
                Console.WriteLine($"[+] Share '{def.Name}' ({def.Id}) -> {def.Path}");
            }

            return shares;
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

                try
                {
                    SyncFromDb();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SyncFromDb ERROR] {ex.Message}");
                }
            });
        }

        // ═══════════════════════ DYNAMIC SHARE MANAGEMENT ═══════════════════════
        //
        // All mutations go through _shareLock so operations complete atomically. No server restart —
        // existing SMB connections stay alive; only new tree connects see the change.

        /// <summary>Adds a new share at runtime. Immediately accessible for new tree connects.</summary>
        public void AddShare(Guid shareId, string name, string path, bool isHidden = false)
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

                _server.AddShare(CreateShare(shareId, name, path, isHidden));
                _activeShares[name] = new ShareEntry { Id = shareId, DbName = name, Path = path, IsHidden = isHidden };

                Console.WriteLine($"[+] Share '{name}' ({shareId}) -> {path} [live hinzugefügt]");
            }
        }

        /// <summary>
        /// Removes a share at runtime. Active tree connections remain functional until the client
        /// disconnects — only new tree connects are blocked.
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
        /// Updates an existing share (path changed, renamed, etc.). Internally removes the old and adds
        /// the new share. Active connections to the old share remain until the client disconnects.
        /// </summary>
        public void UpdateShare(string oldName, Guid shareId, string newName, string newPath, bool isHidden = false)
        {
            lock (_shareLock)
            {
                if (_server == null)
                    throw new InvalidOperationException("SMB server is not running.");

                if (_activeShares.TryRemove(oldName, out _))
                    _server.RemoveShare(oldName);

                _server.AddShare(CreateShare(shareId, newName, newPath, isHidden));
                _activeShares[newName] = new ShareEntry { Id = shareId, DbName = newName, Path = newPath, IsHidden = isHidden };

                Console.WriteLine($"[~] Share aktualisiert: '{oldName}' -> '{newName}' ({shareId}) -> {newPath}");
            }
        }

        /// <summary>
        /// Full resync with the database. Adds new shares, removes deleted ones, replaces changed ones.
        /// No server restart, no connection interruption. All changes happen atomically under one lock.
        /// </summary>
        public void SyncFromDb()
        {
            lock (_shareLock)
            {
                if (_server == null)
                    return;

                using var scope = _serviceProvider.CreateScope();
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareRepository>();
                var dbShares = SmbSync.Run(() => shareRepo.GetAllEnabledAsync());

                var dbByName = new Dictionary<string, (Guid Id, string Name, string Path, bool IsHidden)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var s in dbShares)
                    dbByName[s.Name] = (s.Id, s.Name, s.Path, s.IsShareHidden);

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

                // Add new or replace changed shares
                foreach (var (id, name, path, isHidden) in dbByName.Values)
                {
                    if (_activeShares.TryGetValue(name, out var existing))
                    {
                        // Path or hidden-flag changed → replace
                        if (!string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)
                            || existing.IsHidden != isHidden)
                        {
                            _activeShares.TryRemove(name, out _);
                            _server.RemoveShare(name);

                            _server.AddShare(CreateShare(id, name, path, isHidden));
                            _activeShares[name] = new ShareEntry { Id = id, DbName = name, Path = path, IsHidden = isHidden };

                            Console.WriteLine($"[~] Share '{name}' aktualisiert -> {path}");
                        }
                    }
                    else
                    {
                        _server.AddShare(CreateShare(id, name, path, isHidden));
                        _activeShares[name] = new ShareEntry { Id = id, DbName = name, Path = path, IsHidden = isHidden };
                        Console.WriteLine($"[+] Share '{name}' ({id}) -> {path} [sync hinzugefügt]");
                    }
                }

                Console.WriteLine($"[*] DB-Sync abgeschlossen: {_activeShares.Count} aktive Shares");
            }
        }

        // ═══════════════════════ SHARE FACTORY ═══════════════════════

        private KaimoShare CreateShare(Guid shareId, string name, string path, bool isHidden)
        {
            Directory.CreateDirectory(path);

            var fileService = _fileServiceFactory.CreateForShare(shareId, path);
            return new KaimoShare
            {
                Name = name,
                ShareId = shareId,
                IsHidden = isHidden,
                FileStore = new KaimoFileStore(shareId, fileService),
            };
        }

        // ═══════════════════════ DISPOSE ═══════════════════════

        public void Dispose() => Stop();
    }
}
