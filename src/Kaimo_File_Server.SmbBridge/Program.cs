using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.SmbBridge.Security;
using Kaimo_File_Server.SmbBridge.Services;
using Kaimo_File_Server.Core.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Kaimo SMB Bridge: lean gRPC control plane for the Samba container.
// Phase 1 only provides the auth part (NT hashes for Samba's tdbsam),
// backed by existing Core/Infrastructure services.
var builder = WebApplication.CreateBuilder(args);

// Reuse existing Kaimo services (DB, repos, AuthenticationLookup,
// NtHashProtector). Needs the same NtHash:EncryptionKey as Host/Web.
builder.Services.AddInfrastructure(builder.Configuration);
builder.AddDynamicLogLevel();
builder.AddLogArchive("smb-bridge");

// Real Elasticsearch search service — MUST come before AddCoreServices so it wins
// the NoOp fallback (Phase 3: SMB writes are indexed like web uploads).
builder.Services.AddElasticSearch(builder.Configuration);



var baseStoragePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
var applicationDataPath = builder.Configuration.GetValue<string>("Storage:ApplicationDataPath") ?? "/data/kaimo-system";
var poolStoragePaths = Directory.GetDirectories(baseStoragePath).Select(path => path).ToList();

builder.Services.AddCoreServices(poolStoragePaths, applicationDataPath);

// Config store (like in Host): needed transitively by ILoginService/ManagementAuth
// and therefore registered for DI validation.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

var controlPlaneTls = ControlPlaneTls.Load(builder.Configuration);
builder.Services.AddSingleton(controlPlaneTls);
builder.Services.AddSingleton<ControlPlaneAuthorizationInterceptor>();
builder.Services.Configure<HashExportRateLimitOptions>(
    builder.Configuration.GetSection(HashExportRateLimitOptions.SectionName));
builder.Services.AddSingleton<HashExportRateLimiter>();
builder.Services.AddGrpc(options =>
    options.Interceptors.Add<ControlPlaneAuthorizationInterceptor>());

// Phase 5 hardening: bound the isolated @GMT snapshot materialization cache
// (<cache-root>/<share-id>) so it cannot grow without limit. Evicts by age
// (Snapshots:Cache:TtlHours) and per-share size cap (Snapshots:Cache:MaxBytesPerShare).
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<SnapshotCacheOptions>,
    SnapshotCacheOptionsValidator>();
builder.Services.AddOptions<SnapshotCacheOptions>()
    .Bind(builder.Configuration.GetSection(SnapshotCacheOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<SnapshotCacheLeaseManager>();
builder.Services.AddSingleton<SnapshotMaterializationLimiter>();
builder.Services.AddHostedService<SnapshotCacheCleanupService>();
builder.Services.AddHostedService<SambaEventReceiptCleanupService>();

// The bridge is reachable only on its dedicated Compose control network, and
// every connection must also present a client certificate issued by the
// dedicated control-plane CA. Authorization is narrowed per certificate role
// by ControlPlaneAuthorizationInterceptor.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5080, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
        listen.UseHttps(https =>
        {
            https.ServerCertificate = controlPlaneTls.ServerCertificate;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation =
                controlPlaneTls.ValidateClientCertificate;
        });
    });
});

var app = builder.Build();

// Preflight: verify every mounted data directory is writable by the non-root
// container user before serving. The bridge does not mount the backup volume, so
// that path is excluded.
Kaimo_File_Server.Infrastructure.Startup.WritableDirectoryCheck.VerifyFromConfiguration(
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Kaimo_File_Server.Infrastructure.Startup"),
    builder.Configuration,
    includeBackups: false);

// The Host process owns the schema (migrations + seeding). The bridge must not
// migrate; it waits until the Host has finished before serving requests, so its
// P1-11 receipt tables and everything else are guaranteed to exist.
await app.WaitForDatabaseReadyAsync();

app.MapGrpcService<AuthGrpcService>();       // Phase 1: NT-Hashes (GetNtHash/ListUsers)
app.MapGrpcService<AuthzGrpcService>();      // Phase 2: Autorisierung (Connect/Open)
app.MapGrpcService<FileEventGrpcService>();  // Phase 3: Close-Hooks (Version/Index/Ownership)
app.MapGrpcService<ShareGrpcService>();      // Phase 4: Share-Provisioning (ListShares -> net conf)
app.MapGrpcService<ConfigGrpcService>();     // Phase 4: Protokoll-Settings (GetProtocolSettings -> net conf global) + Enabled-Flag (Phase 5)
app.MapGrpcService<SnapshotGrpcService>();   // Phase 5: ACL-filtered @GMT snapshots/materialization
app.MapGet("/", () => "Kaimo SMB Bridge (mutually authenticated gRPC on :5080).");

app.Run();
