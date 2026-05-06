using Kaimo_File_Server_Core.Core.Repositories;
using Kaimo_File_Server_Core.Core.Security;
using Kaimo_File_Server_Core.Core.Services;
using Kaimo_File_Server_Core.Core.Storage;
using Kaimo_File_Server_Core.Infrastructure;
using Kaimo_File_Server_Core.Infrastructure.Services;
using Kaimo_File_Server_Core.Smb;
using Kaimo_File_Server_Core.Storage;

var builder = Host.CreateApplicationBuilder(args);

// ── Infrastructure (DB + Repositories) ──
builder.Services.AddInfrastructure(builder.Configuration);

// ── Core Services ──
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";

builder.Services.AddSingleton<IStorageEngine>(sp => new FileSystemStorage(storagePath, sp));
builder.Services.AddSingleton<IAclService, AclService>();
builder.Services.AddScoped<IUserContextFactory, UserContextFactory>();

// Interface-Registrierung: alle Konsumenten hängen an IFileService
builder.Services.AddSingleton<IFileService, FileService>();

// ── SMB Transport ──
builder.Services.AddSingleton<SmbServer>();

var host = builder.Build();
await host.InitializeDatabaseAsync();

var smbServer = host.Services.GetRequiredService<SmbServer>();
await smbServer.StartAsync(CancellationToken.None);

host.Run();