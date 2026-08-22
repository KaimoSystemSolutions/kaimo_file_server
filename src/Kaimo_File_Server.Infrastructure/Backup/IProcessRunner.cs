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
}
