using Kaimo_File_Server.Core.Domain;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards that <see cref="CloudSyncAdvancedSettings.RootLevelPermissionsOnly"/> is
/// carried by every value-semantics path — JSON round-trip (the persisted column),
/// <see cref="CloudSyncAdvancedSettings.Clone"/> (used by the legacy→first-class
/// migration), and equality (used by the editor's dirty-check).
/// </summary>
public class CloudSyncAdvancedSettingsTests
{
    [Fact]
    public void JsonRoundTrip_PreservesRootLevelPermissionsOnly()
    {
        var settings = new CloudSyncAdvancedSettings { RootLevelPermissionsOnly = true };

        var restored = SyncDefinition.DeserializeAdvancedSettings(
            SyncDefinition.SerializeAdvancedSettings(settings));

        Assert.True(restored.RootLevelPermissionsOnly);
    }

    [Fact]
    public void Deserialize_LegacyPayloadWithoutField_DefaultsToFalse()
    {
        var restored = SyncDefinition.DeserializeAdvancedSettings("{\"SyncDeletions\":true}");

        Assert.False(restored.RootLevelPermissionsOnly);
        Assert.True(restored.SyncDeletions);
    }

    [Fact]
    public void CloneAndEquality_DistinguishRootLevelPermissionsOnly()
    {
        var on = new CloudSyncAdvancedSettings { RootLevelPermissionsOnly = true };
        var off = new CloudSyncAdvancedSettings { RootLevelPermissionsOnly = false };

        Assert.Equal(on, on.Clone());
        Assert.True(on.Clone().RootLevelPermissionsOnly);
        Assert.NotEqual(on, off);
    }
}
