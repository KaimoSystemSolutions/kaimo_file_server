using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Kaimo SMB Bridge: lean gRPC control plane for the Samba container.
// Phase 1 only provides the auth part (NT hashes for Samba's tdbsam),
// backed by existing Core/Infrastructure services.
var builder = WebApplication.CreateBuilder(args);

// Reuse existing Kaimo services (DB, repos, AuthenticationLookup,
// NtHashProtector). Needs the same NtHash:EncryptionKey as Host/Web.
builder.Services.AddInfrastructure(builder.Configuration);

// Real Elasticsearch search service — MUST come before AddCoreServices so it wins
// the NoOp fallback (Phase 3: SMB writes are indexed like web uploads).
builder.Services.AddElasticSearch(builder.Configuration);

var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// Config store (like in Host): needed transitively by ILoginService/ManagementAuth
// and therefore registered for DI validation.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

builder.Services.AddGrpc();

// Phase 5 hardening: bound the @GMT snapshot materialization cache
// (<share>/.kaimo-snapshots) so it cannot grow without limit. Evicts by age
// (Snapshots:Cache:TtlHours) and per-share size cap (Snapshots:Cache:MaxBytesPerShare).
builder.Services.AddHostedService<SnapshotCacheCleanupService>();

// gRPC over HTTP/2 in plaintext (h2c) on internal Docker network — no TLS needed,
// the bridge is not exposed externally.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5080, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();

app.MapGrpcService<AuthGrpcService>();       // Phase 1: NT-Hashes (GetNtHash/ListUsers)
app.MapGrpcService<AuthzGrpcService>();      // Phase 2: Autorisierung (Connect/Open)
app.MapGrpcService<FileEventGrpcService>();  // Phase 3: Close-Hooks (Version/Index/Ownership)
app.MapGrpcService<ShareGrpcService>();      // Phase 4: Share-Provisioning (ListShares -> net conf)
app.MapGrpcService<ConfigGrpcService>();     // Phase 4: Protokoll-Settings (GetProtocolSettings -> net conf global) + Enabled-Flag (Phase 5)
app.MapGrpcService<SnapshotGrpcService>();   // Phase 5: ACL-filtered @GMT snapshots/materialization
app.MapGet("/", () => "Kaimo SMB Bridge (gRPC/h2c on :5080). Use a gRPC client.");

app.Run();
