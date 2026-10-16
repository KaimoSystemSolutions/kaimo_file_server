using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// Collects the per-entry failures of one reindex pass. Only the first few are logged as
/// warnings (with path and stack trace); the rest go to Debug and the pass ends with a single
/// summary warning grouped by cause, so a pass over thousands of failing files stays readable.
/// </summary>
internal sealed class ReindexFailureLog(ILogger logger)
{
    internal const int DetailedWarnings = 5;

    private readonly Dictionary<string, int> _byCause = new();
    private int _count;

    public void Add(string path, Exception ex)
    {
        if (++_count <= DetailedWarnings)
            logger.LogWarning(ex, "Reindex: indexing failed for '{Path}'", path);
        else
            logger.LogDebug(ex, "Reindex: indexing failed for '{Path}'", path);

        var cause = ex.GetBaseException().GetType().Name;
        _byCause[cause] = _byCause.GetValueOrDefault(cause) + 1;
    }

    public void LogSummary(int total)
    {
        if (_count <= DetailedWarnings)
            return;

        logger.LogWarning(
            "Reindex: {Failed} of {Total} entries could not be indexed ({Causes}). The first {Shown} " +
            "are logged above; the others only at Debug level.",
            _count, total,
            string.Join(", ", _byCause.OrderByDescending(c => c.Value).Select(c => $"{c.Key} ×{c.Value}")),
            DetailedWarnings);
    }
}
