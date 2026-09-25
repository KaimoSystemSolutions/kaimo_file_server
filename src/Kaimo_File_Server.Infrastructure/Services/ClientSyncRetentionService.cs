using Kaimo_File_Server.Core.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Periodically prunes the append-only client-sync tables so they cannot grow without bound:
/// the <c>file_change_log</c> delta feed, the idempotency <c>client_request_receipts</c>, and
/// expired <c>refresh_tokens</c>. Every table already carries the index its cutoff scans; this
/// service is the missing scheduler that actually invokes the prune.
///
/// The change-log window is deliberately generous (default 90 days): a client offline for longer
/// re-bootstraps via a full <c>delta</c> — the <c>changes</c> feed's <c>reset</c> flag signals that
/// gap — so pruning it is safe rather than silently lossy.
/// </summary>
public sealed class ClientSyncRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ClientSyncRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int changeLogDays = Clamp("ClientSync:ChangeLogRetentionDays", 90);
        int receiptDays = Clamp("ClientSync:RequestReceiptRetentionDays", 30);
        // Grace beyond a refresh token's own expiry; the token is unusable past expiry regardless.
        int refreshTokenDays = Clamp("ClientSync:RefreshTokenRetentionDays", 7);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var now = DateTime.UtcNow;

                int changeLog = await sp.GetRequiredService<IFileChangeLogRepository>()
                    .PruneOlderThanAsync(now.AddDays(-changeLogDays), stoppingToken);
                int receipts = await sp.GetRequiredService<IClientRequestReceiptRepository>()
                    .PruneOlderThanAsync(now.AddDays(-receiptDays));
                int refreshTokens = await sp.GetRequiredService<IRefreshTokenRepository>()
                    .PruneExpiredBeforeAsync(now.AddDays(-refreshTokenDays));
                // A revoked web token is unusable past its own expiry, so the entry can go then.
                int revokedWebTokens = await sp.GetRequiredService<IRevokedWebTokenRepository>()
                    .PruneExpiredBeforeAsync(now);

                if (changeLog > 0 || receipts > 0 || refreshTokens > 0 || revokedWebTokens > 0)
                    logger.LogInformation(
                        "Client-sync retention pruned {ChangeLog} change-log entries, " +
                        "{Receipts} request receipts, {RefreshTokens} expired refresh tokens, " +
                        "{RevokedWebTokens} expired web-token revocations.",
                        changeLog, receipts, refreshTokens, revokedWebTokens);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Client-sync retention pruning failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private int Clamp(string key, int fallback)
        => Math.Clamp(configuration.GetValue(key, fallback), 1, 3650);
}
