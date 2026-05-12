using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Storage;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Storage;
using Kaimo_File_Server.Web.Components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();

// ── JWT ──
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());

// ── Infrastructure (DB + Repositories) ──
builder.Services.AddInfrastructure(builder.Configuration);

// ── Core Services ──
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddSingleton<IStorageEngine>(sp => new FileSystemStorage(storagePath, sp));
builder.Services.AddSingleton<IAclService, AclService>();
builder.Services.AddScoped<IFileService, FileService>();

// ── ViewModels ──
builder.Services.AddScoped<LoginViewModel>();
builder.Services.AddScoped<ShareListViewModel>();
builder.Services.AddScoped<FileBrowserViewModel>();

// ── DataProtection ──
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/data/storage/.dp-keys"))
    .SetApplicationName("KaimoFiles");

var app = builder.Build();

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

await app.InitializeDatabaseAsync();

app.Run();