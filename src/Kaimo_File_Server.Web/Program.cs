using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureKaimoKestrel();

// ══════════════════════════════════════════
//  Shared infrastructure (DB, repos, core services)
//  — registered ONCE via extension methods
// ══════════════════════════════════════════

builder.Services.AddInfrastructure(builder.Configuration);

// -- Global, live-reloadable log level (shared with the SMB host via the DB) --
builder.AddDynamicLogLevel();

// -- Structured Information+ archive on the dedicated bind mount --
builder.AddLogArchive("web");

// ══════════════════════════════════════════
//  Elastic Search
//  -> needs to replace the NoOPSearch engine in core, before core is loaded
// ══════════════════════════════════════════

builder.Services.AddElasticSearch(builder.Configuration);

var baseStoragePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
var applicationDataPath = builder.Configuration.GetValue<string>("Storage:ApplicationDataPath") ?? "/data/kaimo-system";
var poolStoragePaths = Directory.GetDirectories(baseStoragePath).Select(path => path).ToList();
Kaimo_File_Server.Infrastructure.Services.SharePoolMoveService.DeleteStaleReservations(poolStoragePaths);

builder.Services.AddCoreServices(poolStoragePaths, applicationDataPath);

builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

builder.AddKaimoBlazorAndControllers();
builder.AddKaimoAuthentication();
builder.AddKaimoWebServices(applicationDataPath);
builder.AddKaimoViewModels(poolStoragePaths);
var dataProtectionCertificatePath = builder.AddKaimoDataProtection(applicationDataPath);
builder.AddKaimoBackgroundJobs(applicationDataPath);

var app = builder.Build();

await app.RunKaimoStartupTasksAsync(dataProtectionCertificatePath);
app.UseKaimoPipeline();
await app.ApplyDefaultCultureAsync();

app.Run();
