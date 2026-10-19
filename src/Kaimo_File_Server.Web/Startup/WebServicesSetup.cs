using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>Web-only services: UI services, external storage providers, cloud sync, single-owner background services and download ticket stores.</summary>
internal static class WebServicesSetup
{
    public static void AddKaimoWebServices(this WebApplicationBuilder builder, string applicationDataPath)
    {
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
        // Removes managed SSH known_hosts files of connections that no longer exist.
        builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.ExternalStorage.ManagedKnownHostsCleanupHostedService>();
        // Prunes the append-only client-sync tables (change log, request receipts, expired refresh tokens)
        // so they cannot grow without bound.
        builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Services.ClientSyncRetentionService>();
        // Owns all Elasticsearch index writes by tailing the change log, so file operations never block on
        // ES. Single-owner: registered only here (the web host), never in the SMB/host processes.
        builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Services.SearchIndexingService>();
        // Enforces recycle-bin retention and size limits for every transport. Single-owner like the indexer.
        builder.Services.AddScoped<Kaimo_File_Server.Infrastructure.Services.RecycleBinJanitor>();
        builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Services.RecycleBinJanitorService>();
        // Turns outbox notification events into mails and sends them. Single-owner like the indexer:
        // every process publishes, only the web host (which can decrypt the SMTP password) dispatches.
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Notifications.ISmtpMailSender,
            Kaimo_File_Server.Web.Services.Notifications.MailKitSmtpMailSender>();
        builder.Services.AddSingleton<Kaimo_File_Server.Web.Services.Notifications.NotificationMailComposer>();
        builder.Services.AddHostedService<Kaimo_File_Server.Web.Services.Notifications.NotificationDispatcherService>();
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
    }
}
