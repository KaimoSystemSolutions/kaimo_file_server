using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>One-time work after the host is built and before requests are served.</summary>
internal static class StartupTasks
{
    public static async Task RunKaimoStartupTasksAsync(this WebApplication app, string? dataProtectionCertificatePath)
    {
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
                preflightLogger, app.Configuration, includeBackups: true);

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
    }

    public static async Task ApplyDefaultCultureAsync(this WebApplication app)
    {
        // Seed the AppDomain default culture from the configured language so Blazor
        // interactive circuits (whose continuations run off the request pipeline) don't
        // intermittently fall back to English. Kept in sync by SaveLanguageAsync.
        using (var cultureScope = app.Services.CreateScope())
        {
            var cultureConfig = cultureScope.ServiceProvider.GetRequiredService<IConfigRepository>();
            ConfigLocalizationMiddleware.ApplyDefaultCulture(
                await cultureConfig.GetStringAsync("app.language", "de"));
        }
    }
}
