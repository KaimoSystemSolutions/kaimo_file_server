using System.Text;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class LogArchiveTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-log-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Provider_archives_information_despite_global_warning_filter()
    {
        var options = Options.Create(new LogArchiveOptions
        {
            RootPath = _root,
            Source = "web",
            Instance = "test-instance"
        });
        using var provider = new LogArchiveLoggerProvider(options);
        await provider.StartAsync(CancellationToken.None);
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter<LogArchiveLoggerProvider>(
                (_, level) => level >= LogLevel.Information && level != LogLevel.None);
            logging.AddProvider(provider);
        });

        var logger = factory.CreateLogger("Kaimo.Tests.Archive");
        logger.LogDebug("not archived");
        using (logger.BeginScope("outer-scope"))
        using (logger.BeginScope("inner-scope"))
            logger.LogInformation(new EventId(42, "ArchiveTest"), "stored value {Value}", 123);
        logger.LogError(new InvalidOperationException("broken"), "failure");
        await provider.StopAsync(CancellationToken.None);

        var reader = new FileLogArchiveReader(options);
        var result = await reader.QueryAsync(new LogArchiveQuery(["web"]));

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.EventId == 42
            && entry.Properties?["Value"] == "123"
            && entry.Scopes!.SequenceEqual(["outer-scope", "inner-scope"]));
        Assert.Contains(result.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Exception?.Contains("InvalidOperationException") == true);
    }

    [Fact]
    public async Task Reader_filters_search_and_streams_complete_download()
    {
        var sourceDirectory = Path.Combine(_root, "host", "2026", "08", "02");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, "host-test-20260802T000000Z-000.ndjson");
        var lines = new[]
        {
            "{\"timestampUtc\":\"2026-08-02T00:00:00Z\",\"sequence\":1,\"level\":\"Information\",\"service\":\"host\",\"instance\":\"a\",\"category\":\"Worker\",\"eventId\":0,\"message\":\"started\"}",
            "{\"timestampUtc\":\"2026-08-02T00:00:01Z\",\"sequence\":2,\"level\":\"Warning\",\"service\":\"host\",\"instance\":\"a\",\"category\":\"Worker\",\"eventId\":0,\"message\":\"disk almost full\"}",
            "{\"timestampUtc\":\"2026-08-02T00:00:02Z\",\"sequence\":3,\"level\":\"Error\",\"service\":\"host\",\"instance\":\"a\",\"category\":\"Worker\",\"eventId\":0,\"message\":\"disk full\"}"
        };
        await File.WriteAllLinesAsync(path, lines);
        var options = Options.Create(new LogArchiveOptions { RootPath = _root });
        var reader = new FileLogArchiveReader(options);

        var result = await reader.QueryAsync(new LogArchiveQuery(
            ["host"], LogLevel.Warning, "disk", Limit: 1));
        Assert.Single(result.Entries);
        Assert.True(result.HasMore);
        Assert.Equal(LogLevel.Error, result.Entries[0].Level);

        await using var download = new MemoryStream();
        await reader.WriteDownloadAsync(
            new LogArchiveQuery(["host"], LogLevel.Warning, "disk"), download);
        var text = Encoding.UTF8.GetString(download.ToArray());
        Assert.DoesNotContain("started", text);
        Assert.Contains("disk almost full", text);
        Assert.Contains("disk full", text);
    }

    [Fact]
    public async Task Reader_reads_newest_entries_across_multiple_tail_blocks()
    {
        var sourceDirectory = Path.Combine(_root, "web", "2026", "08", "02");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, "web-test-20260802T000000Z-000.ndjson");
        await using (var stream = File.CreateText(path))
        {
            for (var sequence = 1; sequence <= 2500; sequence++)
            {
                await stream.WriteLineAsync(
                    $"{{\"timestampUtc\":\"2026-08-02T00:00:00Z\",\"sequence\":{sequence}," +
                    "\"level\":\"Information\",\"service\":\"web\",\"instance\":\"a\"," +
                    $"\"category\":\"Worker\",\"eventId\":0,\"message\":\"entry-{sequence:D4}-" +
                    new string('x', 80) + "\"}");
            }
        }

        var reader = new FileLogArchiveReader(Options.Create(new LogArchiveOptions { RootPath = _root }));
        var result = await reader.QueryAsync(new LogArchiveQuery(["web"], Limit: 3));

        Assert.Equal([2500L, 2499L, 2498L], result.Entries.Select(entry => entry.Sequence));
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task Reader_limits_query_and_download_to_selected_utc_day()
    {
        var firstDay = Path.Combine(_root, "samba", "2026", "08", "01");
        var secondDay = Path.Combine(_root, "samba", "2026", "08", "02");
        Directory.CreateDirectory(firstDay);
        Directory.CreateDirectory(secondDay);
        await File.WriteAllTextAsync(
            Path.Combine(firstDay, "samba-test-20260801T000000Z-000.ndjson"),
            "{\"timestampUtc\":\"2026-08-01T23:59:59Z\",\"sequence\":1,\"level\":\"Warning\",\"service\":\"samba\",\"instance\":\"a\",\"category\":\"Samba\",\"eventId\":0,\"message\":\"older\"}\n");
        await File.WriteAllTextAsync(
            Path.Combine(secondDay, "samba-test-20260802T000000Z-000.ndjson"),
            "{\"timestampUtc\":\"2026-08-02T00:00:00Z\",\"sequence\":2,\"level\":\"Warning\",\"service\":\"samba\",\"instance\":\"a\",\"category\":\"Samba\",\"eventId\":0,\"message\":\"newer\"}\n");

        var reader = new FileLogArchiveReader(Options.Create(new LogArchiveOptions { RootPath = _root }));
        var date = new DateOnly(2026, 8, 1);
        var result = await reader.QueryAsync(new LogArchiveQuery(["samba"], UtcDate: date));
        Assert.Single(result.Entries);
        Assert.Equal("older", result.Entries[0].Message);

        await using var download = new MemoryStream();
        await reader.WriteDownloadAsync(new LogArchiveQuery(["samba"], UtcDate: date), download);
        var text = Encoding.UTF8.GetString(download.ToArray());
        Assert.Contains("older", text);
        Assert.DoesNotContain("newer", text);
    }

    [Fact]
    public async Task Reader_returns_empty_result_when_archive_does_not_exist()
    {
        var reader = new FileLogArchiveReader(Options.Create(new LogArchiveOptions { RootPath = _root }));

        Assert.Empty(await reader.GetSourcesAsync());
        var result = await reader.QueryAsync(new LogArchiveQuery(["samba"]));
        Assert.Empty(result.Entries);
        Assert.False(result.HasMore);

        await using var download = new MemoryStream();
        await reader.WriteDownloadAsync(
            new LogArchiveQuery(["samba"], UtcDate: new DateOnly(2026, 8, 2)), download);
        Assert.Empty(download.ToArray());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}
