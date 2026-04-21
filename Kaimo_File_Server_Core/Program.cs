using Kaimo_File_Server_Core;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Core.Storage;
using Kaimo_File_Server_Core.Smb;
using Kaimo_File_Server_Core.Storage;

var builder = Host.CreateApplicationBuilder(args);

var storage = new FileSystemStorage("/data/storage");
var aclService = new AclService();
var fileService = new FileService(storage, aclService);
var userContextAccessor = new UserContextAccessor();

SmbFileSystem smbFileSystem = new("/data/storage", fileService, userContextAccessor);
SmbServer smbServer = new SmbServer(smbFileSystem);
smbServer.StartAsync(CancellationToken.None).Wait();

//builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<IStorageEngine>(new FileSystemStorage(@"/data/files"));

var host = builder.Build();
host.Run();
