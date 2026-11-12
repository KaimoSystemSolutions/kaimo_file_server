namespace Kaimo_File_Server.Core.Security.Demo;

/// <summary>
/// Process-wide read-only demo flag (env var <c>KAIMO_DEMO_READONLY</c>). Injected
/// wherever the UI needs to know it is a demo — to hide write actions and show the
/// feedback banner. The actual write blocking lives at the two chokepoints
/// (<see cref="ReadOnlyDemoAclService"/> and the EF save/command interceptor); this
/// flag only drives presentation.
/// </summary>
public sealed class DemoModeOptions
{
    public bool ReadOnly { get; init; }
}

/// <summary>
/// Thrown when a write is attempted while the process runs in read-only demo mode
/// (env var <c>KAIMO_DEMO_READONLY=true</c>). Surfaces at the two write chokepoints:
/// the EF save/command interceptor (all DB + settings) and the ACL guard (files).
/// </summary>
public sealed class ReadOnlyDemoException : Exception
{
    public ReadOnlyDemoException()
        : base("This server runs in read-only demo mode; changes are not permitted.") { }
}
