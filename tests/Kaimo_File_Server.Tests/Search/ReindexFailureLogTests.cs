using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A reindex pass that fails on thousands of files must not write one warning per file: only the
/// first few failures are warnings, the rest go to Debug, and one summary warning closes the pass.
/// </summary>
public sealed class ReindexFailureLogTests
{
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void ManyFailures_LogFirstFewAsWarnings_RestAsDebug_PlusOneSummary()
    {
        var logger = new ListLogger();
        var failures = new ReindexFailureLog(logger);

        for (int i = 0; i < 1000; i++)
            failures.Add($"/data/f{i}.txt", i % 2 == 0 ? new TimeoutException("t") : new IOException("io"));
        failures.LogSummary(total: 5000);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(ReindexFailureLog.DetailedWarnings + 1, warnings.Count);
        Assert.Contains("/data/f0.txt", warnings[0].Message);
        var summary = warnings[^1].Message;
        Assert.Contains("1000", summary);
        Assert.Contains("5000", summary);
        Assert.Contains("TimeoutException", summary);
        Assert.Contains("IOException", summary);
        Assert.Equal(1000 - ReindexFailureLog.DetailedWarnings,
            logger.Entries.Count(e => e.Level == LogLevel.Debug));
    }

    [Fact]
    public void NoFailures_LogsNothing()
    {
        var logger = new ListLogger();

        new ReindexFailureLog(logger).LogSummary(total: 10);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FewFailures_AreAllWarnings_WithoutSummary()
    {
        var logger = new ListLogger();
        var failures = new ReindexFailureLog(logger);

        failures.Add("/data/a.txt", new IOException("locked"));
        failures.LogSummary(total: 10);

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);
    }
}
