using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Kaimo_File_Server.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Infrastructure.Logging;

/// <summary>
/// Non-blocking structured archive provider. Application threads only format and enqueue;
/// one background writer owns rotation, flushing and retention for this process.
/// </summary>
[ProviderAlias("LogArchive")]
public sealed class LogArchiveLoggerProvider : ILoggerProvider, ISupportExternalScope, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LogArchiveOptions _options;
    private readonly Channel<LogArchiveEntry> _channel;
    private readonly ConcurrentDictionary<string, LogArchiveLogger> _loggers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private Task? _writerTask;
    private long _sequence;
    private long _dropped;

    public LogArchiveLoggerProvider(IOptions<LogArchiveOptions> options)
    {
        _options = options.Value;
        _options.Normalize();
        _channel = Channel.CreateBounded<LogArchiveEntry>(
            new BoundedChannelOptions(_options.ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, category => new LogArchiveLogger(this, category));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _writerTask ??= Task.Run(() => RunWriterAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_writerTask is null)
            return;

        try
        {
            await _writerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _stop.Cancel();
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _stop.Cancel();
        _stop.Dispose();
    }

    private void Enqueue<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (level < LogLevel.Information || level == LogLevel.None)
            return;

        Dictionary<string, string?>? properties = null;
        if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
        {
            foreach (var (key, value) in structuredState)
            {
                if (key == "{OriginalFormat}")
                    continue;
                properties ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                properties[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        List<string>? scopes = null;
        _scopeProvider.ForEachScope((scope, _) =>
        {
            var text = Convert.ToString(scope, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(text))
            {
                scopes ??= new List<string>();
                scopes.Add(text);
            }
        }, state: (object?)null);

        var activity = Activity.Current;
        var entry = new LogArchiveEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Sequence = Interlocked.Increment(ref _sequence),
            Level = level,
            Service = _options.Source,
            Instance = _options.Instance!,
            Category = category,
            EventId = eventId.Id,
            EventName = eventId.Name,
            Message = formatter(state, exception),
            Exception = exception?.ToString(),
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            Properties = properties,
            Scopes = scopes
        };

        _channel.Writer.TryWrite(entry);
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        ArchiveFileWriter? writer = null;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                try
                {
                    while (_channel.Reader.TryRead(out var entry))
                    {
                        writer ??= new ArchiveFileWriter(_options, JsonOptions);
                        await writer.WriteAsync(entry, cancellationToken);
                    }

                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                    {
                        writer ??= new ArchiveFileWriter(_options, JsonOptions);
                        await writer.WriteAsync(new LogArchiveEntry
                        {
                            TimestampUtc = DateTimeOffset.UtcNow,
                            Sequence = Interlocked.Increment(ref _sequence),
                            Level = LogLevel.Warning,
                            Service = _options.Source,
                            Instance = _options.Instance!,
                            Category = typeof(LogArchiveLoggerProvider).FullName!,
                            Message = $"{dropped} archive log entries were dropped because the queue was full.",
                            Properties = new Dictionary<string, string?> { ["DroppedCount"] = dropped.ToString() }
                        }, cancellationToken);
                    }

                    if (writer is not null)
                        await writer.FlushAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[log-archive] Write failed; retrying in 5 seconds: {exception.Message}");
                    if (writer is not null)
                    {
                        try { await writer.DisposeAsync(); } catch { }
                        writer = null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
    }

    private sealed class LogArchiveLogger(
        LogArchiveLoggerProvider provider,
        string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => provider._scopeProvider.Push(state);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Enqueue(category, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class ArchiveFileWriter : IAsyncDisposable
    {
        private readonly LogArchiveOptions _options;
        private readonly JsonSerializerOptions _jsonOptions;
        private StreamWriter? _writer;
        private FileStream? _stream;
        private DateOnly _date;
        private string? _currentPath;
        private DateOnly _lastCleanupDate;

        public ArchiveFileWriter(LogArchiveOptions options, JsonSerializerOptions jsonOptions)
        {
            _options = options;
            _jsonOptions = jsonOptions;
        }

        public async Task WriteAsync(LogArchiveEntry entry, CancellationToken cancellationToken)
        {
            var date = DateOnly.FromDateTime(entry.TimestampUtc.UtcDateTime);
            if (_writer is null || date != _date || (_stream?.Length ?? 0) >= _options.MaxFileBytes)
                await RotateAsync(date, entry.TimestampUtc, cancellationToken);

            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            await _writer!.WriteLineAsync(json.AsMemory(), cancellationToken);
        }

        public Task FlushAsync(CancellationToken cancellationToken)
            => _writer?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

        private async Task RotateAsync(
            DateOnly date,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            await DisposeWriterAsync();
            var directory = Path.Combine(
                _options.RootPath,
                _options.Source,
                date.Year.ToString("0000"),
                date.Month.ToString("00"),
                date.Day.ToString("00"));
            Directory.CreateDirectory(directory);

            var prefix = $"{_options.Source}-{_options.Instance}-{timestamp.UtcDateTime:yyyyMMddTHHmmssZ}";
            var sequence = 0;
            string path;
            do
            {
                path = Path.Combine(directory, $"{prefix}-{sequence++:000}.ndjson");
            } while (File.Exists(path));

            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            _date = date;
            _currentPath = path;

            if (_lastCleanupDate != date)
            {
                try { CleanupOldFiles(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _lastCleanupDate = date;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private void CleanupOldFiles()
        {
            var sourceRoot = Path.Combine(_options.RootPath, _options.Source);
            if (!Directory.Exists(sourceRoot))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            var files = Directory.EnumerateFiles(sourceRoot, "*.ndjson", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, _currentPath, StringComparison.Ordinal))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
            {
                TryDelete(file);
            }

            files = files.Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            var retainedBytes = files.Sum(file => file.Length);
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (retainedBytes <= _options.MaxBytesPerSource)
                    break;
                var length = file.Length;
                if (TryDelete(file))
                    retainedBytes -= length;
            }
        }

        private static bool TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private async Task DisposeWriterAsync()
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync();
                await _writer.DisposeAsync();
                _writer = null;
            }
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }
        }

        public async ValueTask DisposeAsync() => await DisposeWriterAsync();
    }
}
