using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Smb;

var builder = Host.CreateApplicationBuilder(args);

// -- Infrastructure (DB + Repositories + AuthenticationLookup) --
builder.Services.AddInfrastructure(builder.Configuration);

// -- Core Services (FileService, AclService, StorageEngine) --
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// -- Config store (desired-state flags for data services, shared with the Web UI) --
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

// -- SMB Transport (registered as an IManagedDataService) --
builder.Services.AddSmb(builder.Configuration);

// -- Reconciler: drives Start/Stop of all managed data services from config flags --
builder.Services.AddHostedService<Kaimo_File_Server.Host.DataServiceReconciler>();


var host = builder.Build();
await host.InitializeDatabaseAsync();

host.Run();
