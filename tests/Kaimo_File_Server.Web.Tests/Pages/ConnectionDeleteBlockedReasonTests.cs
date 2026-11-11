using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Web.Components.Pages.ExternalStorage.Components;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>The delete action's tooltip names every consumer that blocks deleting a connection.</summary>
public sealed class ConnectionDeleteBlockedReasonTests
{
    [Fact]
    public void NotInUse_HasNoReason()
        => Assert.Null(ConnectionDisplay.DeleteBlockedReason(new StorageConnectionUsage(0, 0)));

    [Fact]
    public void BackupOnly_NamesBackupRepositories()
    {
        var reason = ConnectionDisplay.DeleteBlockedReason(new StorageConnectionUsage(0, 0, BackupRepositoryCount: 2));

        Assert.NotNull(reason);
        Assert.Contains($"{Resources.ResourceManager.GetString("Web_StorageConnection_UsedByBackups")}: 2", reason);
        Assert.DoesNotContain(Resources.ResourceManager.GetString("Web_ExternalStorage_Tab_Syncs")!, reason);
    }

    [Fact]
    public void AllConsumers_AreListedWithCounts()
    {
        var reason = ConnectionDisplay.DeleteBlockedReason(new StorageConnectionUsage(3, 1, 2));

        Assert.NotNull(reason);
        Assert.Contains($"{Resources.ResourceManager.GetString("Web_ExternalStorage_Tab_Syncs")}: 3", reason);
        Assert.Contains($"{Resources.ResourceManager.GetString("Web_StorageConnection_UsedByVirtualShares")}: 1", reason);
        Assert.Contains($"{Resources.ResourceManager.GetString("Web_StorageConnection_UsedByBackups")}: 2", reason);
    }
}
