using Kaimo_File_Server.Core.Repositories.Files;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Security.Acl;
using Kaimo_File_Server.Core.Services.CloudSync;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Versioning;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>ViewModels (scoped per Blazor circuit). ManagementAuthService, UserContextFactory and ShareLockManager are registered by AddInfrastructure / AddCoreServices; do not re-register them here.</summary>
internal static class ViewModelSetup
{
    public static void AddKaimoViewModels(this WebApplicationBuilder builder, IReadOnlyList<string> poolStoragePaths)
    {
        builder.Services.AddScoped<SettingsViewModel>();
        builder.Services.AddScoped<LogViewerViewModel>();
        builder.Services.AddScoped<ClientConnectionInfo>();
        builder.Services.AddScoped<LoginViewModel>();
        builder.Services.AddScoped<ShareBrowserViewModel>();
        builder.Services.AddScoped<FileBrowserViewModel>();
        builder.Services.AddScoped<PublicShareFileBrowserViewModel>();
        builder.Services.AddScoped<HomeFileBrowserViewModel>();
        builder.Services.AddScoped<HomesAdminViewModel>();
        builder.Services.AddScoped<Kaimo_File_Server.Web.Services.ShareLinkService>();
        builder.Services.AddScoped<Kaimo_File_Server.Web.Services.PublicUploadService>();
        builder.Services.AddSingleton<ShareLinkTokenProtector>();
        builder.Services.AddScoped<ShareLinkListViewModel>();
        builder.Services.AddScoped<SecurityMonitorViewModel>();
        builder.Services.AddScoped<UserListViewModel>();
        builder.Services.AddScoped<Kaimo_File_Server.Web.Components.ProfileNavigator>();
        builder.Services.AddScoped<AclEditorViewModel>();
        // One principal list for all circuits; repositories are scoped, so each reload opens a scope.
        builder.Services.AddSingleton(sp => new PrincipalDirectoryCache(async () =>
        {
            await using var scope = sp.CreateAsyncScope();
            var s = scope.ServiceProvider;
            return await PrincipalDirectoryCache.LoadAsync(
                s.GetRequiredService<IUserRepository>(),
                s.GetRequiredService<IGroupRepository>(),
                s.GetRequiredService<IRoleRepository>());
        }, minRevalidateInterval: TimeSpan.FromSeconds(2)));
        builder.Services.AddScoped<DepartmentViewModel>();
        builder.Services.AddScoped<ExternalStorageSyncViewModel>();
        builder.Services.AddScoped<HomeDashboardViewModel>();
        builder.Services.AddScoped<ClientDeviceAdminViewModel>();
        builder.Services.AddScoped<FileServiceAccessViewModel>();
        builder.Services.AddScoped<MailServerSettingsViewModel>();
        builder.Services.AddScoped<AccessSettingsViewModel>();
        builder.Services.AddScoped<LoginProtectionSettingsViewModel>();
        builder.Services.AddScoped<SmbLockViewModel>();
        builder.Services.AddScoped<UserSecondFactorViewModel>();
        builder.Services.AddScoped<SecondFactorSettingsViewModel>();
        builder.Services.AddScoped<NotificationsViewModel>();
        builder.Services.AddScoped<CloudAccessViewModel>();
        builder.Services.AddScoped<CloudAccessShareBrowserViewModel>();
        builder.Services.AddScoped<RemoteCloudAccessFileBrowserViewModel>();

        // Share pool moves (share management and home folders). The search index is not written
        // from here: indexing is owned by SearchIndexingService.
        builder.Services.AddScoped(sp => new Kaimo_File_Server.Infrastructure.Services.SharePoolMoveService(
            sp.GetRequiredService<IShareRepository>(),
            sp.GetRequiredService<ShareLockManager>(),
            sp.GetRequiredService<ILogger<Kaimo_File_Server.Infrastructure.Services.SharePoolMoveService>>(),
            searchService: null,
            sp.GetRequiredService<ICloudSyncOperationCoordinator>()));

        // ShareListViewModel receives configured pool destinations. File I/O itself
        // always uses the absolute path persisted on the selected share.
        builder.Services.AddScoped<ShareListViewModel>(sp =>
            new ShareListViewModel(
                sp.GetRequiredService<IShareRepository>(),
                sp.GetRequiredService<IUserRepository>(),
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
                departmentRepo: sp.GetRequiredService<IDepartmentRepository>(),
                poolMove: sp.GetRequiredService<Kaimo_File_Server.Infrastructure.Services.SharePoolMoveService>()));
    }
}
