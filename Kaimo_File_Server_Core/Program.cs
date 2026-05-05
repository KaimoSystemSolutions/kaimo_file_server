using Kaimo_File_Server_Core;
using Kaimo_File_Server_Core.Core.Repositories.Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Core.Storage;
using Kaimo_File_Server_Core.Infrastructure;
using Kaimo_File_Server_Core.Infrastructure.Services;
using Kaimo_File_Server_Core.Smb;
using Kaimo_File_Server_Core.Storage;

var builder = Host.CreateApplicationBuilder(args);

// Infrastructure (DB + DBRepos)
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<IUserContextFactory, UserContextFactory>();

// Core 
var storage = new FileSystemStorage("/data/storage");
var aclService = new AclService();
var fileService = new FileService(storage, aclService);
var userContextAccessor = new UserContextAccessor();



//builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<IStorageEngine>(new FileSystemStorage(@"/data/files"));

var host = builder.Build();
await host.InitializeDatabaseAsync();

// Smb
var smbServer = new SmbServer(host.Services, fileService, userContextAccessor);
smbServer.StartAsync(CancellationToken.None).Wait();

host.Run();
