using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Services.Https;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>HTTPS certificate (self-signed, auto-renewed), the cloud sync runner/scheduler and restic file backup (Web only: the vault that holds repository passwords lives here).</summary>
internal static class BackgroundJobsSetup
{
    public static void AddKaimoBackgroundJobs(this WebApplicationBuilder builder, string applicationDataPath)
    {
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

        // restic file backup (Web only: the vault that holds repository passwords lives here).
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.ResticClient>();
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.ResticTargetResolver>();
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupJobExecutor>();
        // Singleton like the runner: versions and change log go through the per-call-scope wrappers.
        builder.Services.AddSingleton(sp => new Kaimo_File_Server.Infrastructure.Backup.Restic.BackupRestoreExecutor(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Kaimo_File_Server.Infrastructure.Persistence.ApplicationDbContext>>(),
            sp.GetRequiredService<Kaimo_File_Server.Infrastructure.Backup.Restic.ResticTargetResolver>(),
            sp.GetRequiredService<Kaimo_File_Server.Infrastructure.Backup.Restic.ResticClient>(),
            new ScopedFileVersionService(sp),
            new ScopedFileChangeLog(sp),
            sp.GetRequiredService<ShareLockManager>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupRestoreExecutor>>()));
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupMaintenanceExecutor>();
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupRunner>();
        builder.Services.AddSingleton<Kaimo_File_Server.Infrastructure.Backup.Restic.IBackupRunner>(
            sp => sp.GetRequiredService<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupRunner>());
        builder.Services.AddHostedService(
            sp => sp.GetRequiredService<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupRunner>());
        builder.Services.AddHostedService<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupSchedulerService>();
        builder.Services.AddScoped<Kaimo_File_Server.Infrastructure.Backup.Restic.BackupCatalogService>();
        builder.Services.AddScoped<Kaimo_File_Server.Web.Components.ViewModels.Backup.BackupViewModel>();
    }
}
