using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Smb;

var builder = Host.CreateApplicationBuilder(args);

// -- Infrastructure (DB + Repositories + AuthenticationLookup) --
builder.Services.AddInfrastructure(builder.Configuration);

// -- Core Services (FileService, AclService, StorageEngine) --
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// -- SMB Transport --
builder.Services.AddSmb(builder.Configuration);

// -- Search -- 
builder.Services.AddElasticSearch(builder.Configuration);

var host = builder.Build();
await host.InitializeDatabaseAsync();

var smbServer = host.Services.GetRequiredService<SmbServer>();
await smbServer.StartAsync(CancellationToken.None);

host.Run();
