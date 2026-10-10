namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>Result of running an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Thin abstraction over launching an external executable (e.g. pg_dump /
/// pg_restore). Kept behind an interface so the backup logic can be unit-tested
/// without a real PostgreSQL client on the machine.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments and extra
    /// environment variables, waits for it to exit, and returns its result.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a long-lived process whose stdout is streamed line by line to
    /// <paramref name="onStdoutLine"/> (the returned <see cref="ProcessResult.StandardOutput"/>
    /// is then empty) and whose stderr is kept only as a bounded tail. With
    /// <paramref name="clearEnvironment"/> the child does not inherit this process's
    /// environment (which holds e.g. the database connection string); only PATH, HOME,
    /// locale and temp variables are carried over before <paramref name="environment"/> is applied.
    /// </summary>
    Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStdoutLine,
        bool clearEnvironment,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This process runner does not support streaming.");

    /// <summary>
    /// Like <see cref="RunStreamingAsync"/>, but copies stdout unchanged (binary) into
    /// <paramref name="stdoutTarget"/>, e.g. an archive streamed to an HTTP response.
    /// Cancellation kills the process.
    /// </summary>
    Task<ProcessResult> RunToStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Stream stdoutTarget,
        bool clearEnvironment,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This process runner does not support binary streaming.");
}
