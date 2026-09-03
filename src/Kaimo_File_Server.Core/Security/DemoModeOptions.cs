namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Process-wide read-only demo flag (env var <c>KAIMO_DEMO_READONLY</c>). Injected
/// wherever the UI needs to know it is a demo — to hide write actions and show the
/// feedback banner. The actual write blocking lives at the two chokepoints
/// (<see cref="ReadOnlyDemoAclService"/> and the EF SaveChanges interceptor); this
/// flag only drives presentation.
/// </summary>
public sealed class DemoModeOptions
{
    public bool ReadOnly { get; init; }
}
