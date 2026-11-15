using Kaimo_File_Server.Infrastructure.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Startup;

/// <summary>
/// Pins the Host's SMB control-plane PKI bootstrap: the generator receives the
/// PKI directory, and a missing or failing generator never stops the Host
/// (it owns the database migration; only the SMB bridge depends on the PKI).
/// </summary>
public sealed class ControlPlanePkiBootstrapTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("kaimo-pki-bootstrap");

    public void Dispose() => _work.Delete(recursive: true);

    [Fact]
    public void Run_GeneratorSucceeds_PassesDirectoryAndReturnsTrue()
    {
        string pki = Path.Combine(_work.FullName, "pki");
        Directory.CreateDirectory(pki);
        string generator = WriteGenerator(
            windows: "@echo generated> \"%~1\\marker\"",
            unix: "echo generated > \"$1/marker\"");

        bool ready = ControlPlanePkiBootstrap.Run(
            NullLogger.Instance, generator, pki, TimeSpan.FromSeconds(30));

        Assert.True(ready);
        Assert.True(File.Exists(Path.Combine(pki, "marker")));
    }

    [Fact]
    public void Run_GeneratorFails_ReturnsFalseWithoutThrowing()
    {
        string generator = WriteGenerator(
            windows: "@echo refusing to overwrite incomplete PKI 1>&2\r\n@exit /b 1",
            unix: "echo 'refusing to overwrite incomplete PKI' >&2; exit 1");

        bool ready = ControlPlanePkiBootstrap.Run(
            NullLogger.Instance, generator, _work.FullName, TimeSpan.FromSeconds(30));

        Assert.False(ready);
    }

    [Fact]
    public void Run_GeneratorSucceedsWithStderr_LogsWarning()
    {
        // e.g. a certificate that expires soon but cannot be renewed here.
        string generator = WriteGenerator(
            windows: "@echo warning: samba.crt expires soon 1>&2",
            unix: "echo 'warning: samba.crt expires soon' >&2");
        var logger = new CapturingLogger();

        bool ready = ControlPlanePkiBootstrap.Run(
            logger, generator, _work.FullName, TimeSpan.FromSeconds(30));

        Assert.True(ready);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("expires soon"));
    }

    [Fact]
    public void Run_GeneratorSucceedsWithOpensslProgress_LogsInformation()
    {
        string generator = WriteGenerator(
            windows: "@echo Certificate request self-signature ok 1>&2",
            unix: "echo 'Certificate request self-signature ok' >&2");
        var logger = new CapturingLogger();

        ControlPlanePkiBootstrap.Run(logger, generator, _work.FullName, TimeSpan.FromSeconds(30));

        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
    }

    [Fact]
    public void Run_GeneratorMissing_ReturnsFalseWithoutThrowing()
    {
        bool ready = ControlPlanePkiBootstrap.Run(
            NullLogger.Instance,
            Path.Combine(_work.FullName, "does-not-exist"),
            _work.FullName,
            TimeSpan.FromSeconds(30));

        Assert.False(ready);
    }

    private string WriteGenerator(string windows, string unix)
    {
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(_work.FullName, "generator.cmd");
            File.WriteAllText(script, windows + "\r\n");
            return script;
        }

        string shell = Path.Combine(_work.FullName, "generator.sh");
        File.WriteAllText(shell, "#!/bin/sh\n" + unix + "\n");
        File.SetUnixFileMode(shell,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
