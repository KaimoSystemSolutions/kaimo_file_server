using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.DependencyInjection;


var builder = Host.CreateApplicationBuilder(args);

// -- Infrastructure (DB + Repositories + AuthenticationLookup) --
builder.Services.AddInfrastructure(builder.Configuration);

// -- Global, live-reloadable log level (shared with the Web UI via the DB) --
builder.AddDynamicLogLevel();

// -- Structured Information+ archive on the dedicated bind mount --
builder.AddLogArchive("host");

// -- Elastic Search --
//    Must be registered BEFORE AddCoreServices so the real search service wins
//    over the NoOp fallback (AddCoreServices uses TryAddSingleton). Without this
//    the Host runs on NoOpSearchService and files written via SMB are never
//    indexed -> search never returns hits for SMB-served files.
builder.Services.AddElasticSearch(builder.Configuration);

// -- Core Services. Share I/O resolves through ShareDefinition.Path. --
var baseStoragePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
var applicationDataPath = builder.Configuration.GetValue<string>("Storage:ApplicationDataPath") ?? "/data/kaimo-system";
var poolStoragePaths = Directory.GetDirectories(baseStoragePath).Select(path => path).ToList();

builder.Services.AddCoreServices(poolStoragePaths, applicationDataPath);

// -- Config store (desired-state flags for data services, shared with the Web UI) --
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

// -- SMB service control (Phase 5 cutover) --
//    The SMB protocol layer now lives in the separate Samba container (kaimo_samba);
//    the real on/off enforcement is the bridge's connect hook (deny-all when disabled).
//    This adapter keeps the "Datendienste" toggle/status meaningful — the reconciler
//    still drives it from services.smb.enabled — without an in-process SMB server.
builder.Services.AddSingleton<IManagedDataService, Kaimo_File_Server.Host.SambaSmbControlService>();

// -- Reconciler: drives Start/Stop of all managed data services from config flags --
builder.Services.AddHostedService<Kaimo_File_Server.Host.DataServiceReconciler>();

// -- Scheduled database backups (Host is the sole DB owner) --
builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Backup.DatabaseBackupSchedulerService>();



var host = builder.Build();

// Preflight: verify the service can actually write to every mounted data
// directory before touching the database or files. Bind mounts created with root
// ownership silently block the non-root container user, so this surfaces one
// clear, actionable failure at boot instead of a cryptic error at first write.
Kaimo_File_Server.Infrastructure.Startup.WritableDirectoryCheck.VerifyFromConfiguration(
    host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Kaimo_File_Server.Infrastructure.Startup"),
    builder.Configuration,
    includeBackups: true);

// Host owns the database: apply the one-shot startup restore (if requested),
// take a pre-migration safety backup, migrate + seed, then signal readiness.
await host.MigrateSeedAndBackupAsync();

// Ensure the index exists with the correct (n-gram) mapping before the SMB
// server starts writing. Otherwise the first indexed document would let ES
// auto-create the index with a default mapping, breaking partial-word search.
var search = host.Services.GetRequiredService<ISearchService>();
await search.InitializeAsync();

host.Run();
