using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════
//  Shared infrastructure (DB, repos, core services)
//  — registered ONCE via extension methods
// ══════════════════════════════════════════

builder.Services.AddInfrastructure(builder.Configuration);

var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// ══════════════════════════════════════════
//  Blazor + Auth
// ══════════════════════════════════════════

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();

// ── JWT ──
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());

// ══════════════════════════════════════════
//  Web-only services
// ══════════════════════════════════════════

builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<FileUploadCoordinator>();
builder.Services.AddSingleton<AssetProvider>();

// ══════════════════════════════════════════
//  ViewModels
//
//  NOTE: ManagementAuthService, UserContextFactory, ShareLockManager
//  are already registered by AddInfrastructure / AddCoreServices.
//  Do NOT re-register them here.
// ══════════════════════════════════════════

builder.Services.AddScoped<LoginViewModel>();
builder.Services.AddScoped<ShareBrowserViewModel>();
builder.Services.AddScoped<FileBrowserViewModel>();
builder.Services.AddScoped<UserListViewModel>();
builder.Services.AddScoped<AclEditorViewModel>();

// ShareListViewModel needs the storagePath string — use a factory lambda.
builder.Services.AddScoped<ShareListViewModel>(sp =>
    new ShareListViewModel(
        sp.GetRequiredService<IShareRepository>(),
        sp.GetRequiredService<IShareAccessRepository>(),
        sp.GetRequiredService<IUserRepository>(),
        sp.GetRequiredService<IGroupRepository>(),
        sp.GetRequiredService<IStorageEngine>(),
        sp.GetRequiredService<ShareLockManager>(),
        sp.GetRequiredService<AuthenticationStateProvider>(),
        sp.GetRequiredService<ILogger<ShareListViewModel>>(),
        storagePath));

// ══════════════════════════════════════════
//  DataProtection
// ══════════════════════════════════════════

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/data/storage/.dp-keys"))
    .SetApplicationName("KaimoFiles");

// ══════════════════════════════════════════
//  Build & configure pipeline
// ══════════════════════════════════════════

var app = builder.Build();

// BUG FIX: DB was never initialized in the Web project.
// Without this, tables and seed data are missing when Web starts
// independently of the Host project.
await app.InitializeDatabaseAsync();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();