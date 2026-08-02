using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Core.Logging;

public static class LogArchiveQueryLimits
{
    public const int MaxExcludedMessagePrefixes = 5;
    public const int MaxExcludedMessagePrefixLength = 200;
}

/// <summary>A structured entry persisted as one line in the log archive.</summary>
public sealed class LogArchiveEntry
{
    public DateTimeOffset TimestampUtc { get; init; }
    public long Sequence { get; init; }
    public LogLevel Level { get; init; }
    public string Service { get; init; } = "";
    public string Instance { get; init; } = "";
    public string Category { get; init; } = "";
    public int EventId { get; init; }
    public string? EventName { get; init; }
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public IReadOnlyDictionary<string, string?>? Properties { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>Server-side filters for archive queries and downloads.</summary>
public sealed record LogArchiveQuery(
    IReadOnlyCollection<string> Sources,
    LogLevel MinimumLevel = LogLevel.Information,
    string? SearchText = null,
    int Limit = 1000,
    DateOnly? UtcDate = null,
    IReadOnlyCollection<string>? ExcludedMessagePrefixes = null);

public sealed record LogArchiveQueryResult(
    IReadOnlyList<LogArchiveEntry> Entries,
    bool HasMore);

public interface ILogArchiveReader
{
    Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default);

    Task<LogArchiveQueryResult> QueryAsync(
        LogArchiveQuery query,
        CancellationToken cancellationToken = default);

    Task WriteDownloadAsync(
        LogArchiveQuery query,
        Stream destination,
        CancellationToken cancellationToken = default);
}
