using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Kaimo SMB Bridge: schlanke gRPC-Control-Plane fuer den Samba-Container.
// Phase 1 stellt nur den Auth-Teil bereit (NT-Hashes fuer Sambas tdbsam),
// gestuetzt auf die bestehenden Core-/Infrastructure-Services.
var builder = WebApplication.CreateBuilder(args);

// Bestehende Kaimo-Dienste wiederverwenden (DB, Repos, AuthenticationLookup,
// NtHashProtector). Braucht denselben NtHash:EncryptionKey wie Host/Web.
builder.Services.AddInfrastructure(builder.Configuration);

// Echter Elasticsearch-Suchdienst — MUSS vor AddCoreServices kommen, damit er
// den NoOp-Fallback gewinnt (Phase 3: SMB-Writes werden indiziert wie Web-Uploads).
builder.Services.AddElasticSearch(builder.Configuration);

var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// Config store (wie im Host): wird transitiv von ILoginService/ManagementAuth
// benoetigt und daher fuer die DI-Validierung registriert.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

builder.Services.AddGrpc();

// gRPC ueber HTTP/2 im Klartext (h2c) im internen Docker-Netz - kein TLS noetig,
// die Bridge ist nicht nach aussen exponiert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5080, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();

app.MapGrpcService<AuthGrpcService>();       // Phase 1: NT-Hashes (GetNtHash/ListUsers)
app.MapGrpcService<AuthzGrpcService>();      // Phase 2: Autorisierung (Connect/Open)
app.MapGrpcService<FileEventGrpcService>();  // Phase 3: Close-Hooks (Version/Index/Ownership)
app.MapGet("/", () => "Kaimo SMB Bridge (gRPC/h2c on :5080). Use a gRPC client.");

app.Run();
