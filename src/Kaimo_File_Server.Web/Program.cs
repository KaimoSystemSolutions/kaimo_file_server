using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Api;
using Kaimo_File_Server.Web.Services.Https;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

builder.Services.AddControllers()
    // Serialize/accept enums (e.g. SyncMode) as their names in the client API JSON.
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpClient(nameof(OneDriveDeviceAuthorizationService), client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("CloudAccessOneDrive", client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(nameof(DropboxAuthorizationService), client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("CloudAccessDropbox", client =>
    client.Timeout = Timeout.InfiniteTimeSpan);

// -- JWT --
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());

// -- JWT bearer authentication for the client REST API (/api/v1). Uses the SAME
//    validation parameters as the Blazor token path (JwtTokenService) so the two
//    can never drift apart. The Blazor localStorage flow is unaffected — it does
//    not depend on this handler, and its token is not accepted here. --
// Validated eagerly here (not only in the lazily created JwtTokenService) so the bearer
// handler can never run with a weak, well-known or development-only signing secret.
var apiJwtSecret = JwtTokenService.ValidateSecret(
    builder.Configuration["Jwt:Secret"],
    allowDevelopmentSecret: builder.Environment.IsDevelopment());
var apiJwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "KaimoFileServer";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            JwtTokenService.CreateValidationParameters(apiJwtSecret, apiJwtIssuer);
        // On /dav the WebDAV Basic scheme owns the 401 challenge (Basic realm only —
        // the Windows redirector aborts a mount offered a scheme it cannot satisfy),
        // so the bearer handler must not also add a `WWW-Authenticate: Bearer` header
        // there. Bearer authentication itself still works on /dav.
        options.Events = new JwtBearerEvents
        {
            // Single choke point for every bearer-authenticated endpoint (/api/v1, /dav):
            // device-scoped tokens only, active device, enabled account, current security stamp.
            OnTokenValidated = BearerTokenValidation.ValidateAsync,
            OnChallenge = context =>
            {
                if (context.Request.Path.StartsWithSegments(WebDavPathResolver.Prefix))
                    context.HandleResponse();
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, WebDavBasicAuthenticationHandler>(
        WebDavBasicAuthenticationHandler.SchemeName, _ => { });

// WebDAV runs in-process as a routed branch of this pipeline (see WebDavController).
// A five-second-cached view of its settings and an in-memory class-2 lock table.
builder.Services.AddSingleton<WebDavOptions>();
builder.Services.AddSingleton<WebDavLockManager>();

// Honor X-Forwarded-Proto/-For so `services.webdav.requireHttps` sees the real
// scheme and the login lockout sees the real client behind a reverse proxy.
// Only peers in ForwardedHeaders:KnownNetworks/KnownProxies are trusted
// (default: loopback + private ranges, i.e. a proxy in the same Docker network).
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(
    options => ForwardedHeadersSetup.Configure(options, builder.Configuration));

// Security overview: records every credential check (web, API, WebDAV) and counts
// API/WebDAV requests per client, persisted in batches by the flush service.
// Wraps the login service registered by AddInfrastructure.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SecurityMonitor>();
builder.Services.AddHostedService<SecurityMonitorFlushService>();
builder.Services.AddScoped<CredentialLoginService>();
builder.Services.AddScoped<ILoginService>(sp => new MonitoredLoginService(
    sp.GetRequiredService<CredentialLoginService>(),
    sp.GetRequiredService<SecurityMonitor>(),
    sp.GetRequiredService<IHttpContextAccessor>()));

// Issues/rotates client-API access + refresh tokens.
builder.Services.AddScoped<ApiTokenService>();

// OpenAPI document for per-platform client code generation (served at /openapi/v1.json).
builder.Services.AddOpenApi("v1");

// ══════════════════════════════════════════
//  Web-only services
// ══════════════════════════════════════════

builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<DateFormatService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddSingleton<LogDownloadTokenService>();
builder.Services.AddSingleton<BackupDownloadTokenService>();
builder.Services.AddScoped<FileUploadCoordinator>();
builder.Services.AddScoped<FileSelectionCoordinator>();
builder.Services.AddSingleton<AssetProvider>();
builder.Services.AddSingleton<ICloudAuthorizationTicketStore, CloudAuthorizationTicketStore>();
builder.Services.AddSingleton<IOneDriveDeviceAuthorizationService, OneDriveDeviceAuthorizationService>();
builder.Services.AddSingleton<IDropboxAuthorizationService, DropboxAuthorizationService>();
builder.Services.AddSingleton<GoogleOAuthService>();
builder.Services.AddSingleton<ICredentialVault, DataProtectionCredentialVault>();
builder.Services.AddExternalStorageProviders(applicationDataPath);
builder.Services.AddScoped<IStorageConnectionProvider>(services =>
    new LegacyCloudStorageConnectionProvider(
        "onedrive",
        "Microsoft OneDrive",
        StorageProviderCapabilities.Browse
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory
        | StorageProviderCapabilities.Sync
        | StorageProviderCapabilities.StableItemIds
        | StorageProviderCapabilities.DelegatedAuthorization,
        new HashSet<StorageAuthorizationMode>
        {
            StorageAuthorizationMode.DeviceCode,
            StorageAuthorizationMode.DelegatedAuthorizationCode,
            StorageAuthorizationMode.ApplicationCredential
        },
        services.GetRequiredService<ICredentialVault>(),
        services.GetRequiredService<IStorageConnectionRepository>(),
        services.GetRequiredService<ICloudProviderFactory>()));
builder.Services.AddScoped<IStorageConnectionProvider>(services =>
    new LegacyCloudStorageConnectionProvider(
        "google",
        "Google Drive",
        StorageProviderCapabilities.Browse
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory
        | StorageProviderCapabilities.Sync
        | StorageProviderCapabilities.DelegatedAuthorization,
        new HashSet<StorageAuthorizationMode>
        {
            StorageAuthorizationMode.DelegatedAuthorizationCode,
            StorageAuthorizationMode.ServiceAccount
        },
        services.GetRequiredService<ICredentialVault>(),
        services.GetRequiredService<IStorageConnectionRepository>(),
        services.GetRequiredService<ICloudProviderFactory>()));
builder.Services.AddScoped<IStorageConnectionProvider>(services =>
    new LegacyCloudStorageConnectionProvider(
        "dropbox",
        "Dropbox",
        StorageProviderCapabilities.Browse
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Write
        | StorageProviderCapabilities.CreateDirectory
        | StorageProviderCapabilities.Sync
        | StorageProviderCapabilities.DelegatedAuthorization,
        new HashSet<StorageAuthorizationMode>
        {
            StorageAuthorizationMode.DelegatedAuthorizationCode
        },
        services.GetRequiredService<ICredentialVault>(),
        services.GetRequiredService<IStorageConnectionRepository>(),
        services.GetRequiredService<ICloudProviderFactory>()));
builder.Services.AddScoped<ILegacyCloudSyncMigrationService, LegacyCloudSyncMigrationService>();
builder.Services.AddScoped<ICloudSyncExecutionService, CloudSyncExecutionService>();
builder.Services.AddHostedService<LegacyCloudSyncMigrationHostedService>();
// Prunes the append-only client-sync tables (change log, request receipts, expired refresh tokens)
// so they cannot grow without bound.
builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Services.ClientSyncRetentionService>();
// Owns all Elasticsearch index writes by tailing the change log, so file operations never block on
// ES. Single-owner: registered only here (the web host), never in the SMB/host processes.
builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Services.SearchIndexingService>();
builder.Services.AddScoped<OneDriveStorageConnectionFactory>();
builder.Services.AddScoped<CredentialRewrapService>();
builder.Services.AddSingleton<CloudAccessDownloadTicketStore>();
builder.Services.AddSingleton<FileDownloadTicketStore>();
builder.Services.AddSingleton<ZipDownloadTicketStore>();
builder.Services.AddSingleton<PublicDownloadTicketStore>();
builder.Services.AddSingleton<ICloudAccessSettingsStore, CloudAccessSettingsStore>();
builder.Services.AddSingleton<CloudAccessDirectoryCache>();
builder.Services.AddScoped<CloudAccessAuthorizationService>();
builder.Services.AddScoped<CloudToLocalTransferService>();
builder.Services.AddScoped<CrossShareTransferService>();
builder.Services.AddScoped<FileBrowserClipboardService>();



// ══════════════════════════════════════════
//  ViewModels
//
//  NOTE: ManagementAuthService, UserContextFactory, ShareLockManager
//  are already registered by AddInfrastructure / AddCoreServices.
//  Do NOT re-register them here.
// ══════════════════════════════════════════

builder.Services.AddScoped<SettingsViewModel>();
builder.Services.AddScoped<LogViewerViewModel>();
builder.Services.AddScoped<ClientConnectionInfo>();
builder.Services.AddScoped<LoginViewModel>();
builder.Services.AddScoped<ShareBrowserViewModel>();
builder.Services.AddScoped<FileBrowserViewModel>();
builder.Services.AddScoped<PublicShareFileBrowserViewModel>();
builder.Services.AddScoped<Kaimo_File_Server.Web.Services.ShareLinkService>();
builder.Services.AddSingleton<ShareLinkTokenProtector>();
builder.Services.AddScoped<ShareLinkListViewModel>();
builder.Services.AddScoped<SecurityMonitorViewModel>();
builder.Services.AddScoped<UserListViewModel>();
builder.Services.AddScoped<Kaimo_File_Server.Web.Components.ProfileNavigator>();
builder.Services.AddScoped<AclEditorViewModel>();
builder.Services.AddScoped<DepartmentViewModel>();
builder.Services.AddScoped<ExternalStorageSyncViewModel>();
builder.Services.AddScoped<ClientDeviceAdminViewModel>();
builder.Services.AddScoped<CloudAccessViewModel>();
builder.Services.AddScoped<CloudAccessShareBrowserViewModel>();
builder.Services.AddScoped<RemoteCloudAccessFileBrowserViewModel>();

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
            sp.GetRequiredService<ICloudSyncOperationCoordinator>(),
        config: sp.GetRequiredService<IConfigRepository>(),
        departmentRepo: sp.GetRequiredService<IDepartmentRepository>()));

// ══════════════════════════════════════════
//  DataProtection
// ══════════════════════════════════════════

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(applicationDataPath, ".dp-keys")))
    .SetApplicationName("KaimoFiles");
// Preferably kept apart from the key ring (DataProtection:CertificatePath, e.g. a Docker
// secret); otherwise generated next to it in the application data (legacy default).
var dataProtectionCertificatePath = builder.Configuration["DataProtection:CertificatePath"];
var dataProtectionCertificate = string.IsNullOrWhiteSpace(dataProtectionCertificatePath)
    ? DataProtectionKeyEncryptionCertificate.LoadOrCreate(applicationDataPath)
    : DataProtectionKeyEncryptionCertificate.LoadFromFile(dataProtectionCertificatePath);
dataProtection.ProtectKeysWithCertificate(dataProtectionCertificate);


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
// One instance is both the job registry the UI reads and the background worker
// that drains the queue, so manual and scheduled syncs share the same runner.
builder.Services.AddSingleton<ICloudSyncJobRunner, CloudSyncJobRunner>();
builder.Services.AddHostedService(sp =>
    (CloudSyncJobRunner)sp.GetRequiredService<ICloudSyncJobRunner>());
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHostedService<CloudSyncSchedulerService>();


// ══════════════════════════════════════════
//  Build & configure pipeline
// ══════════════════════════════════════════

var app = builder.Build();

// Preflight: verify every mounted data directory is writable by the non-root
// container user before serving. Fails fast with one actionable message instead
// of surfacing a cryptic permission error at the first upload/backup/log write.
// Skipped in Development so local runs work without the container's mount layout.
var preflightLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Kaimo_File_Server.Infrastructure.Startup");
if (string.IsNullOrWhiteSpace(dataProtectionCertificatePath) && !app.Environment.IsDevelopment())
    preflightLogger.LogWarning(
        "The Data Protection key-encryption certificate is stored next to the key ring in the application " +
        "data, so a copy of that volume or its backups can decrypt all stored external-storage credentials. " +
        "Move it to a separate secret and set DataProtection:CertificatePath (see the deployment README).");

if (app.Environment.IsDevelopment())
    preflightLogger.LogInformation("Skipping writable-directory preflight in Development.");
else
    Kaimo_File_Server.Infrastructure.Startup.WritableDirectoryCheck.VerifyFromConfiguration(
        preflightLogger, builder.Configuration, includeBackups: true);

// The Host process owns the schema (migrations + seeding). Web must not migrate;
// it waits until the Host has finished so tables and seed data are present.
// Must complete BEFORE search init: the search router reads its on/off flag from
// the config table, which only exists once migrations have run.
await app.WaitForDatabaseReadyAsync();

// Upgrade a bounded number of legacy credential envelopes after migrations
// have completed. Failures leave the original ciphertext untouched and do not
// prevent unrelated providers from starting.
try
{
    await using var rewrapScope = app.Services.CreateAsyncScope();
    var rewrap = await rewrapScope.ServiceProvider
        .GetRequiredService<CredentialRewrapService>()
        .RewrapBatchAsync();
    if (rewrap.Examined > 0)
    {
        app.Logger.LogInformation(
            "Credential rewrap pass examined {Examined}, updated {Rewrapped}, busy {Busy}, concurrently changed {ChangedConcurrently}",
            rewrap.Examined, rewrap.Rewrapped, rewrap.Busy, rewrap.ChangedConcurrently);
    }
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception,
        "The bounded external-storage credential rewrap pass could not complete; existing ciphertext was retained");
}

// Share links created before tokens were hashed still hold their token in plain text;
// replace it by the encrypted form (Data Protection lives here, not in the Host).
try
{
    await using var shareLinkScope = app.Services.CreateAsyncScope();
    int protectedLinks = await app.Services.GetRequiredService<ShareLinkTokenProtector>()
        .ProtectLegacyTokensAsync(shareLinkScope.ServiceProvider.GetRequiredService<IShareLinkRepository>());
    if (protectedLinks > 0)
        app.Logger.LogInformation("Encrypted the plain-text tokens of {Count} share links", protectedLinks);
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception,
        "Legacy share-link tokens could not be encrypted; they stay usable and are retried on the next start");
}

// Load or generate the HTTPS certificate before the first TLS connection is served.
await app.Services.GetRequiredService<HttpsCertificateProvider>().InitializeAsync();

var search = app.Services.GetRequiredService<ISearchService>();
await search.InitializeAsync();

// First in the pipeline so every response — pages, static assets, API, errors —
// carries the security headers (CSP, nosniff, referrer and framing policy).
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();

    var staticWebAssetsManifest = Path.Combine(
        AppContext.BaseDirectory,
        $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json");

    if (File.Exists(staticWebAssetsManifest))
    {
        app.MapStaticAssets();
    }
    else
    {
        // Keep deployments of older/incomplete images operational. Their
        // wwwroot assets can still be served physically even though the
        // MapStaticAssets() manifest is unavailable.
        app.Logger.LogWarning(
            "Static-web-assets manifest {ManifestPath} is missing; using the physical static-file provider.",
            staticWebAssetsManifest);
        app.UseStaticFiles();
    }
}

// Resolve the real client scheme/IP from the reverse proxy before anything reads
// Request.IsHttps (the WebDAV Basic handler's HTTPS requirement depends on it).
app.UseForwardedHeaders();

// Count API/WebDAV requests per real client address (after the forwarded headers).
app.UseMiddleware<SecurityMonitorMiddleware>();

// Authenticate/authorize before antiforgery and endpoints so [Authorize] API
// controllers see the JWT-derived principal. Blazor keeps its own cascading auth.
app.UseAuthentication();
app.UseAuthorization();

// While the WebDAV service is switched off, short-circuit /dav with 503 before the
// endpoint runs. In-process, so no reconciler — the flag is read with a 5s cache.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(WebDavPathResolver.Prefix),
    branch => branch.UseMiddleware<WebDavEnabledMiddleware>());

app.UseAntiforgery();

// Serves the client-API OpenAPI document at /openapi/v1.json for client codegen.
app.MapOpenApi();

app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.UseMiddleware<ConfigLocalizationMiddleware>();

// Seed the AppDomain default culture from the configured language so Blazor
// interactive circuits (whose continuations run off the request pipeline) don't
// intermittently fall back to English. Kept in sync by SaveLanguageAsync.
using (var cultureScope = app.Services.CreateScope())
{
    var cultureConfig = cultureScope.ServiceProvider.GetRequiredService<IConfigRepository>();
    ConfigLocalizationMiddleware.ApplyDefaultCulture(
        await cultureConfig.GetStringAsync("app.language", "de"));
}

app.Run();
