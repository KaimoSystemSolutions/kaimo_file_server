using Kaimo_File_Server.Infrastructure.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pins the startup writability preflight: writable directories pass silently and
/// unwritable ones are aggregated into a single actionable exception naming every
/// offender, so an operator sees the full picture from one boot attempt.
/// </summary>
public sealed class WritableDirectoryCheckTests
{
    [Fact]
    public void VerifyAll_AllWritable_DoesNotThrow()
    {
        var ok = Directory.CreateTempSubdirectory("kaimo-preflight-ok");
        try
        {
            WritableDirectoryCheck.VerifyAll(
                NullLogger.Instance, [("Data", ok.FullName)]);
        }
        finally
        {
            Directory.Delete(ok.FullName, recursive: true);
        }
    }

    [Fact]
    public void VerifyAll_UnwritableDirectory_ThrowsListingEveryFailure()
    {
        var ok = Directory.CreateTempSubdirectory("kaimo-preflight-ok");
        // A path pointing at an existing FILE cannot become a directory, so the
        // probe fails on every OS without relying on filesystem ACLs.
        var blocking = Path.Combine(ok.FullName, "not-a-dir");
        File.WriteAllText(blocking, string.Empty);
        var badPath = Path.Combine(blocking, "child");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                WritableDirectoryCheck.VerifyAll(
                    NullLogger.Instance,
                    [("Data", ok.FullName), ("Backups", badPath)]));

            Assert.Contains("Backups", ex.Message);
            Assert.Contains(badPath, ex.Message);
            Assert.DoesNotContain("Data (", ex.Message); // the writable one is not listed
        }
        finally
        {
            Directory.Delete(ok.FullName, recursive: true);
        }
    }

    [Fact]
    public void FromConfiguration_ListsAppDataLogsPoolsAndBackupsWhenIncluded()
    {
        var storageRoot = Directory.CreateTempSubdirectory("kaimo-storage");
        Directory.CreateDirectory(Path.Combine(storageRoot.FullName, "pool01"));
        Directory.CreateDirectory(Path.Combine(storageRoot.FullName, "pool02"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = storageRoot.FullName,
                    ["Storage:ApplicationDataPath"] = "/data/kaimo-system",
                    ["LogArchive:RootPath"] = "/data/kaimo-logs",
                    ["Backup:RootPath"] = "/data/kaimo-backups",
                })
                .Build();

            var withBackups = WritableDirectoryCheck.FromConfiguration(config, includeBackups: true);
            Assert.Contains(withBackups, d => d.Label == "ApplicationData");
            Assert.Contains(withBackups, d => d.Label == "Logs");
            Assert.Contains(withBackups, d => d.Label == "Backups");
            Assert.Equal(2, withBackups.Count(d => d.Label.StartsWith("Storage:")));

            var withoutBackups = WritableDirectoryCheck.FromConfiguration(config, includeBackups: false);
            Assert.DoesNotContain(withoutBackups, d => d.Label == "Backups");
        }
        finally
        {
            Directory.Delete(storageRoot.FullName, recursive: true);
        }
    }
}
