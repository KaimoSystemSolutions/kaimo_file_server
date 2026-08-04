using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════
//  Kestrel endpoints: plain HTTP (proxy/back-compat) + HTTPS terminated in-process.
//  The HTTPS certificate is resolved per connection from HttpsCertificateProvider,
//  so certificate renewal / replacement takes effect without a restart.
// ══════════════════════════════════════════


builder.WebHost.ConfigureKestrel(options =>
{
    var httpPort = builder.Configuration.GetValue("Kestrel:HttpPort", 8080);
    var httpsPort = builder.Configuration.GetValue("Kestrel:HttpsPort", 8443);

    options.ListenAnyIP(httpPort);
    options.ListenAnyIP(httpsPort, listen => listen.UseHttps(https =>
    {
        var provider = options.ApplicationServices
            .GetRequiredService<HttpsCertificateProvider>();
        https.ServerCertificateSelector = (_, _) => provider.Current;
    }));
});

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

var f = ServiceCollectionExtensions.GetActiveStorageMounts();

builder.Services.AddCoreServices(poolStoragePaths, applicationDataPath);

builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

// ══════════════════════════════════════════
//  Blazor + Auth
// ══════════════════════════════════════════

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();

builder.Services.AddControllers();
builder.Services.AddHttpClient(nameof(OneDriveDeviceAuthorizationService), client =>
    client.Timeout = TimeSpan.FromSeconds(30));

// -- JWT --
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());

// ══════════════════════════════════════════
//  Web-only services
// ══════════════════════════════════════════

builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddSingleton<LogDownloadTokenService>();
builder.Services.AddScoped<FileUploadCoordinator>();
builder.Services.AddScoped<FileSelectionCoordinator>();
builder.Services.AddSingleton<AssetProvider>();
builder.Services.AddSingleton<ICloudAuthorizationTicketStore, CloudAuthorizationTicketStore>();
builder.Services.AddSingleton<IOneDriveDeviceAuthorizationService, OneDriveDeviceAuthorizationService>();



// ══════════════════════════════════════════
//  ViewModels
//
//  NOTE: ManagementAuthService, UserContextFactory, ShareLockManager
//  are already registered by AddInfrastructure / AddCoreServices.
//  Do NOT re-register them here.
// ══════════════════════════════════════════

builder.Services.AddScoped<SettingsViewModel>();
builder.Services.AddScoped<LogViewerViewModel>();
builder.Services.AddScoped<LoginViewModel>();
builder.Services.AddScoped<ShareBrowserViewModel>();
builder.Services.AddScoped<FileBrowserViewModel>();
builder.Services.AddScoped<UserListViewModel>();
builder.Services.AddScoped<AclEditorViewModel>();
builder.Services.AddScoped<DepartmentViewModel>();
builder.Services.AddScoped<CloudSyncViewModel>();

// ShareListViewModel receives configured pool destinations. File I/O itself
// always uses the absolute path persisted on the selected share.
builder.Services.AddScoped<ShareListViewModel>(sp =>
    new ShareListViewModel(
        sp.GetRequiredService<IShareRepository>(),
        sp.GetRequiredService<IUserRepository>(),
        sp.GetRequiredService<IGroupRepository>(),
        sp.GetRequiredService<IAclRepository>(),
        sp.GetRequiredService<IFileMetadataRepository>(),
        sp.GetRequiredService<IAclService>(),
        sp.GetRequiredService<IManagementAuthService>(),
        sp.GetRequiredService<IUserContextFactory>(),
        sp.GetRequiredService<ShareLockManager>(),
        sp.GetRequiredService<AuthenticationStateProvider>(),
        sp.GetRequiredService<ILogger<ShareListViewModel>>(),
        poolStoragePaths,
        sp.GetRequiredService<IFileVersionService>(),
        cloudSyncOperations:
            sp.GetRequiredService<ICloudSyncOperationCoordinator>()));

// ══════════════════════════════════════════
//  DataProtection
// ══════════════════════════════════════════

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(applicationDataPath, ".dp-keys")))
    .SetApplicationName("KaimoFiles");


// ══════════════════════════════════════════
//  HTTPS certificate (self-signed, auto-renewed) + admin management
// ══════════════════════════════════════════

builder.Services.AddSingleton<HttpsCertificateProvider>(sp =>
    new HttpsCertificateProvider(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ISystemInfoService>(),
        sp.GetRequiredService<IDataProtectionProvider>(),
        sp.GetRequiredService<TimeProvider>(),
        applicationDataPath,
        sp.GetRequiredService<ILogger<HttpsCertificateProvider>>()));
builder.Services.AddSingleton<IHttpsCertificateProvider>(
    sp => sp.GetRequiredService<HttpsCertificateProvider>());

builder.Services.AddSingleton<CloudSyncSchedulerSignal>();
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHostedService<CloudSyncSchedulerService>();


// ══════════════════════════════════════════
//  Build & configure pipeline
// ══════════════════════════════════════════

var app = builder.Build();

// BUG FIX: DB was never initialized in the Web project.
// Without this, tables and seed data are missing when Web starts
// independently of the Host project.
// Must run BEFORE search init: the search router reads its on/off flag from the
// config table, which only exists once migrations have run.
await app.InitializeDatabaseAsync();

// Load or generate the HTTPS certificate before the first TLS connection is served.
await app.Services.GetRequiredService<HttpsCertificateProvider>().InitializeAsync();

var search = app.Services.GetRequiredService<ISearchService>();
await search.InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.MapStaticAssets();
}

app.UseAntiforgery();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.UseMiddleware<ConfigLocalizationMiddleware>();


app.Run();
