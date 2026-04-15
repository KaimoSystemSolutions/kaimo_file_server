namespace Kaimo_File_Server_Core
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                    logger.LogInformation("Shit");
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
