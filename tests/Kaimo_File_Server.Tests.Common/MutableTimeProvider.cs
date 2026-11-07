namespace Kaimo_File_Server.Tests.Common;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock can be set/advanced by the test,
/// so lockout-expiry behaviour is deterministic without real waiting.
/// </summary>
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
