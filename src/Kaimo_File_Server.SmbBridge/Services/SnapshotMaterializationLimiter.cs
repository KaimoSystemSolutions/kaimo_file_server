using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Applies a process-wide concurrency limit and immutable per-request budgets
/// before a folder snapshot is allowed to create cache content.
/// </summary>
public sealed class SnapshotMaterializationLimiter
{
    private readonly SemaphoreSlim _concurrency;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxDuration;

    public SnapshotMaterializationLimiter(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _maxFiles = Positive(
            configuration.GetValue("Snapshots:Materialization:MaxFilesPerRequest", 10_000),
            "Snapshots:Materialization:MaxFilesPerRequest");
        _maxBytes = Positive(
            configuration.GetValue(
                "Snapshots:Materialization:MaxBytesPerRequest",
                1024L * 1024 * 1024),
            "Snapshots:Materialization:MaxBytesPerRequest");
        int maxConcurrent = Positive(
            configuration.GetValue(
                "Snapshots:Materialization:MaxConcurrentRequests", 2),
            "Snapshots:Materialization:MaxConcurrentRequests");
        int maxSeconds = Positive(
            configuration.GetValue(
                "Snapshots:Materialization:MaxDurationSeconds", 25),
            "Snapshots:Materialization:MaxDurationSeconds");

        _maxDuration = TimeSpan.FromSeconds(maxSeconds);
        _concurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async ValueTask<Reservation> ReserveAsync(
        IReadOnlyCollection<FileVersion> versions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count > _maxFiles)
            throw new SnapshotMaterializationLimitException(
                $"Folder snapshot contains {versions.Count} files; limit is {_maxFiles}.");

        long bytes = 0;
        foreach (FileVersion version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version.Size < 0)
                throw new InvalidDataException(
                    $"Version '{version.FilePath}' contains a negative size.");
            try
            {
                bytes = checked(bytes + version.Size);
            }
            catch (OverflowException ex)
            {
                throw new SnapshotMaterializationLimitException(
                    "Folder snapshot byte total overflowed.", ex);
            }
            if (bytes > _maxBytes)
                throw new SnapshotMaterializationLimitException(
                    $"Folder snapshot requires {bytes} bytes; limit is {_maxBytes}.");
        }

        var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(_maxDuration);
        try
        {
            await _concurrency.WaitAsync(budget.Token);
            return new Reservation(_concurrency, budget, versions.Count, bytes);
        }
        catch
        {
            budget.Dispose();
            throw;
        }
    }

    internal CancellationTokenSource CreateRequestBudget(
        CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(_maxDuration);
        return budget;
    }

    private static int Positive(int value, string key) =>
        value > 0
            ? value
            : throw new InvalidOperationException($"{key} must be greater than zero.");

    private static long Positive(long value, string key) =>
        value > 0
            ? value
            : throw new InvalidOperationException($"{key} must be greater than zero.");

    public sealed class Reservation : IDisposable
    {
        private SemaphoreSlim? _concurrency;
        private CancellationTokenSource? _budget;

        internal Reservation(
            SemaphoreSlim concurrency,
            CancellationTokenSource budget,
            int fileCount,
            long byteCount)
        {
            _concurrency = concurrency;
            _budget = budget;
            FileCount = fileCount;
            ByteCount = byteCount;
        }

        public int FileCount { get; }
        public long ByteCount { get; }
        public CancellationToken CancellationToken =>
            _budget?.Token
            ?? throw new ObjectDisposedException(nameof(Reservation));

        public void Dispose()
        {
            CancellationTokenSource? budget =
                Interlocked.Exchange(ref _budget, null);
            SemaphoreSlim? concurrency =
                Interlocked.Exchange(ref _concurrency, null);
            budget?.Dispose();
            concurrency?.Release();
        }
    }
}

public sealed class SnapshotMaterializationLimitException : Exception
{
    public SnapshotMaterializationLimitException(string message)
        : base(message)
    {
    }

    public SnapshotMaterializationLimitException(
        string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
