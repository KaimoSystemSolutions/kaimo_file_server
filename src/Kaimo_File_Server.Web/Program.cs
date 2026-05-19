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
using Microsoft.AspNetCore.DataProtection.Repositories;

var builder = WebApplication.CreateBuilder(args);

// -- Infrastructure (DB + Repositories) --
builder.Services.AddInfrastructure(builder.Configuration);

// -- Blazor --
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();

// -- JWT --
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());


// -- Core Services --
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// -- Share Lock Manager (Singleton – ein Lock pro Share, In-Memory) --
builder.Services.AddSingleton<ShareLockManager>();

// -- Additional Repositories --

// -- Services --
builder.Services.AddScoped<ThemeService>();

// -- ViewModels --
builder.Services.AddScoped<LoginViewModel>();
builder.Services.AddScoped<ShareBrowserViewModel>();
builder.Services.AddScoped<FileBrowserViewModel>();
builder.Services.AddScoped<UserListViewModel>();
builder.Services.AddScoped<AclEditorViewModel>();

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

// -- DataProtection --
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


app.Run();