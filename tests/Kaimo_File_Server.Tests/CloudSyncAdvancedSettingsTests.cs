using Kaimo_File_Server.Core.Domain;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards the value-semantics paths of <see cref="CloudSyncAdvancedSettings"/> — the
/// JSON round-trip (the persisted column) and equality (the editor's dirty-check) —
/// and that a legacy payload carrying the removed RootLevelPermissionsOnly field still
/// deserializes cleanly.
/// </summary>
public class CloudSyncAdvancedSettingsTests
{
    [Fact]
    public void JsonRoundTrip_PreservesSyncDeletions()
    {
        var settings = new CloudSyncAdvancedSettings { SyncDeletions = true };

        var restored = SyncDefinition.DeserializeAdvancedSettings(
            SyncDefinition.SerializeAdvancedSettings(settings));

        Assert.True(restored.SyncDeletions);
    }

    [Fact]
    public void Deserialize_LegacyPayloadWithRemovedField_IsIgnored()
    {
        var restored = SyncDefinition.DeserializeAdvancedSettings(
            "{\"SyncDeletions\":true,\"RootLevelPermissionsOnly\":true}");

        Assert.True(restored.SyncDeletions);
    }

    [Fact]
    public void CloneAndEquality_DistinguishSyncDeletions()
    {
        var on = new CloudSyncAdvancedSettings { SyncDeletions = true };
        var off = new CloudSyncAdvancedSettings { SyncDeletions = false };

        Assert.Equal(on, on.Clone());
        Assert.True(on.Clone().SyncDeletions);
        Assert.NotEqual(on, off);
    }
}
