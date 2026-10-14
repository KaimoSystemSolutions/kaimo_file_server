namespace Kaimo_File_Server.Search;

/// <summary>
/// The search engine gave no answer at all (connection refused, timeout) — as opposed to
/// rejecting a single document. Index writes throw this so the change-log indexer stops and
/// retries the entry once the engine is back, instead of dropping the change.
/// </summary>
public sealed class SearchUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
