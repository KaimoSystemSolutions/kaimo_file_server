using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Infrastructure.Logging;

public sealed class FileLogArchiveReader : ILogArchiveReader
{
    private const int MaxViewerRows = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LogArchiveOptions _options;

    public FileLogArchiveReader(IOptions<LogArchiveOptions> options)
    {
        _options = options.Value;
        _options.Normalize();
    }

    public Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_options.RootPath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        try
        {
            IReadOnlyList<string> sources = Directory.EnumerateDirectories(_options.RootPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(name => name == LogArchivePath.SafeSegment(name, ""))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(sources);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    public async Task<LogArchiveQueryResult> QueryAsync(
        LogArchiveQuery query,
        CancellationToken cancellationToken = default)
    {
        query = Normalize(query);
        var limit = Math.Clamp(query.Limit, 1, MaxViewerRows);
        var sources = await ResolveSourcesAsync(query.Sources, cancellationToken);
        var prefilter = LinePrefilter.Build(query);
        var entries = new List<LogArchiveEntry>(Math.Min((limit + 1) * Math.Max(sources.Count, 1), 8192));

        foreach (var source in sources)
        {
            var sourceEntries = 0;
            foreach (var path in EnumerateFiles(source, newestFirst: true, query.UtcDate))
            {
                try
                {
                    await foreach (var line in ReadLinesBackwardAsync(path, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Skip the expensive JSON parse for lines a rare-match filter
                        // (level allow-list or search) cannot possibly match.
                        if (prefilter.CanSkip(line))
                            continue;
                        if (!TryParse(line, out var entry) || !Matches(entry, query))
                            continue;

                        entries.Add(entry);
                        if (++sourceEntries >= limit + 1)
                            break;
                    }
                }
                catch (Exception exception) when (IsUnavailable(exception))
                {
                    // A writer may rotate/delete a chunk between enumeration and opening.
                    // Old chunks may also predate the shared-readable permission policy.
                }
                if (sourceEntries >= limit + 1)
                    break;
            }
        }

        var ordered = entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .ThenByDescending(entry => entry.Sequence)
            .Take(limit + 1)
            .ToList();
        var hasMore = ordered.Count > limit;
        if (hasMore)
            ordered.RemoveAt(ordered.Count - 1);
        return new LogArchiveQueryResult(ordered, hasMore);
    }

    public async Task WriteDownloadAsync(
        LogArchiveQuery query,
        Stream destination,
        bool readable = false,
        CancellationToken cancellationToken = default)
    {
        query = Normalize(query);
        var sources = await ResolveSourcesAsync(query.Sources, cancellationToken);
        var prefilter = LinePrefilter.Build(query);
        await using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(false),
            bufferSize: 64 * 1024,
            leaveOpen: true);

        foreach (var source in sources)
        {
            foreach (var path in EnumerateFiles(source, newestFirst: false, query.UtcDate))
            {
                try
                {
                    await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
                    {
                        if (prefilter.CanSkip(line))
                            continue;
                        if (!TryParse(line, out var entry) || !Matches(entry, query))
                            continue;
                        var output = readable ? FormatReadable(entry) : line;
                        await writer.WriteLineAsync(output.AsMemory(), cancellationToken);
                    }
                }
                catch (Exception exception) when (IsUnavailable(exception))
                {
                    // Continue a multi-source download when one rotated or legacy file
                    // is no longer available.
                }
            }
        }
        await writer.FlushAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ResolveSourcesAsync(
        IReadOnlyCollection<string> requested,
        CancellationToken cancellationToken)
    {
        var available = await GetSourcesAsync(cancellationToken);
        if (requested.Count == 0)
            return [];

        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return available.Where(requestedSet.Contains).ToArray();
    }

    private IEnumerable<string> EnumerateFiles(string source, bool newestFirst, DateOnly? utcDate)
    {
        var safeSource = LogArchivePath.SafeSegment(source, "");
        if (string.IsNullOrEmpty(safeSource) || !string.Equals(safeSource, source, StringComparison.Ordinal))
            return [];

        try
        {
            var sourceDirectory = Path.GetFullPath(Path.Combine(_options.RootPath, safeSource));
            var relative = Path.GetRelativePath(_options.RootPath, sourceDirectory);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return [];

            var directory = utcDate is { } date
                ? Path.Combine(sourceDirectory, date.Year.ToString("0000"), date.Month.ToString("00"), date.Day.ToString("00"))
                : sourceDirectory;
            if (!Directory.Exists(directory))
                return [];

            // Materialize while inside the exception boundary: lazy enumeration can
            // otherwise throw later if retention removes a directory concurrently.
            var files = Directory.EnumerateFiles(directory, "*.ndjson", SearchOption.AllDirectories).ToArray();
            return newestFirst
                ? files.OrderByDescending(path => path, StringComparer.Ordinal).ToArray()
                : files.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return [];
        }
    }

    /// <summary>
    /// Reads UTF-8 NDJSON from the tail in 64 KiB blocks. Newest-log queries therefore
    /// do not rescan a complete active 10 MiB chunk every time the viewer refreshes.
    /// </summary>
    private static async IAsyncEnumerable<string> ReadLinesBackwardAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int blockSize = 64 * 1024;
        const int maxLineBytes = 4 * 1024 * 1024;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            blockSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var buffer = new byte[blockSize];
        var suffix = Array.Empty<byte>();
        var position = stream.Length;
        var trailingLine = true;

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(blockSize, position);
            position -= requested;
            stream.Position = position;
            var read = 0;
            while (read < requested)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(read, requested - read), cancellationToken);
                if (count == 0)
                    break;
                read += count;
            }

            var segmentEnd = read;
            for (var index = read - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n')
                    continue;

                var lineBytes = Combine(buffer, index + 1, segmentEnd - index - 1, suffix, maxLineBytes);
                suffix = Array.Empty<byte>();
                segmentEnd = index;
                if (lineBytes is null)
                    continue;

                var length = lineBytes.Length;
                if (length > 0 && lineBytes[length - 1] == (byte)'\r')
                    length--;
                if (length == 0 && trailingLine)
                {
                    trailingLine = false;
                    continue;
                }

                trailingLine = false;
                yield return Encoding.UTF8.GetString(lineBytes, 0, length);
            }

            suffix = Combine(buffer, 0, segmentEnd, suffix, maxLineBytes) ?? Array.Empty<byte>();
        }

        if (suffix.Length > 0)
        {
            var length = suffix.Length;
            if (suffix[length - 1] == (byte)'\r')
                length--;
            yield return Encoding.UTF8.GetString(suffix, 0, length);
        }
    }

    private static byte[]? Combine(
        byte[] prefixBuffer,
        int prefixOffset,
        int prefixCount,
        byte[] suffix,
        int maxBytes)
    {
        if (prefixCount + suffix.Length > maxBytes)
            return null;
        if (prefixCount == 0)
            return suffix;

        var combined = new byte[prefixCount + suffix.Length];
        Buffer.BlockCopy(prefixBuffer, prefixOffset, combined, 0, prefixCount);
        if (suffix.Length > 0)
            Buffer.BlockCopy(suffix, 0, combined, prefixCount, suffix.Length);
        return combined;
    }

    private static bool TryParse(string line, out LogArchiveEntry entry)
    {
        try
        {
            entry = JsonSerializer.Deserialize<LogArchiveEntry>(line, JsonOptions)!;
            return entry is not null && !string.IsNullOrEmpty(entry.Service);
        }
        catch (JsonException)
        {
            entry = null!;
            return false;
        }
    }

    private static bool Matches(LogArchiveEntry entry, LogArchiveQuery query)
    {
        if (query.UtcDate is { } utcDate
            && DateOnly.FromDateTime(entry.TimestampUtc.UtcDateTime) != utcDate)
            return false;

        if (entry.Level == LogLevel.None)
            return false;
        if (query.Levels is not null)
        {
            if (!query.Levels.Contains(entry.Level))
                return false;
        }
        else if (entry.Level < query.MinimumLevel)
            return false;

        if (query.ExcludedMessagePrefixes?
            .Any(prefix => entry.Message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true)
            return false;

        var search = query.SearchText;
        if (string.IsNullOrEmpty(search))
            return true;

        return Contains(entry.Message, search)
            || Contains(entry.Exception, search)
            || Contains(entry.Category, search)
            || Contains(entry.EventName, search)
            || Contains(entry.Service, search)
            || (entry.Properties?.Any(pair => Contains(pair.Key, search) || Contains(pair.Value, search)) ?? false);
    }

    private static bool Contains(string? value, string search)
        => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    // One human-readable line per entry for the .log export; an exception (if any)
    // follows on its own indented lines so it stays greppable but readable.
    private static string FormatReadable(LogArchiveEntry entry)
    {
        var builder = new StringBuilder(160)
            .Append(entry.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('Z').Append(' ')
            .Append(entry.Level.ToString().ToUpperInvariant().PadRight(11)).Append(' ')
            .Append('[').Append(entry.Service).Append("] ")
            .Append(entry.Category).Append(" — ")
            .Append(entry.Message);
        if (!string.IsNullOrEmpty(entry.Exception))
            builder.Append('\n').Append("    ").Append(entry.Exception.Replace("\n", "\n    "));
        return builder.ToString();
    }

    private static LogArchiveQuery Normalize(LogArchiveQuery query)
    {
        var prefixes = (query.ExcludedMessagePrefixes ?? [])
            .Select(prefix => prefix.Trim())
            .Where(prefix => prefix.Length > 0)
            .Select(prefix => prefix[..Math.Min(prefix.Length, LogArchiveQueryLimits.MaxExcludedMessagePrefixLength)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(LogArchiveQueryLimits.MaxExcludedMessagePrefixes)
            .ToArray();
        return query with
        {
            SearchText = query.SearchText?.Trim(),
            ExcludedMessagePrefixes = prefixes
        };
    }

    private static bool IsUnavailable(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    /// <summary>
    /// A cheap raw-line pre-check that lets a query skip the JSON parse for lines a
    /// rare-match filter cannot match. It is a correct superset of <see cref="Matches"/>:
    /// any kept line is still confirmed there, so a false positive costs one parse,
    /// never a wrong result — while a rare search or level now scans the day instead
    /// of deserializing every info line to find a handful of matches.
    /// </summary>
    private readonly struct LinePrefilter
    {
        private readonly string[]? _levelTokens; // line must contain one of these
        private readonly string? _search;        // line must contain this (case-insensitive)

        private LinePrefilter(string[]? levelTokens, string? search)
        {
            _levelTokens = levelTokens;
            _search = search;
        }

        public bool CanSkip(string line)
        {
            if (_levelTokens is not null && !ContainsAny(line, _levelTokens))
                return true;
            if (_search is not null && !line.Contains(_search, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool ContainsAny(string line, string[] tokens)
        {
            foreach (var token in tokens)
                if (line.Contains(token, StringComparison.Ordinal))
                    return true;
            return false;
        }

        public static LinePrefilter Build(LogArchiveQuery query)
        {
            // Level allow-list of 1–3 of the four on-disk levels: entries are written
            // compact (no spaces) with the PascalCase enum name, so "level":"Error" is
            // a stable exact token. All four selected → nothing to exclude, skip it.
            string[]? levelTokens = null;
            if (query.Levels is { Count: > 0 and < 4 } levels)
                levelTokens = levels.Select(level => $"\"level\":\"{level}\"").ToArray();

            // The default JSON encoder escapes non-ASCII and HTML-sensitive characters
            // (umlauts, <, &, quotes) on disk, so a raw substring match is only valid
            // for plain ASCII terms; anything else falls back to the full parse+match.
            string? search = null;
            var text = query.SearchText;
            if (!string.IsNullOrEmpty(text) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == ' '))
                search = text;

            return new LinePrefilter(levelTokens, search);
        }
    }
}
