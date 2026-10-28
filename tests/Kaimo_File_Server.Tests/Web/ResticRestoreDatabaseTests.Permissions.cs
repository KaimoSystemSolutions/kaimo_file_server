using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// F21: every restore writes the backed-up owners and permissions onto the paths it creates. The
/// manifest is read before anything is moved into the share, so a backup point without readable
/// permissions is refused instead of leaving a copy that only inherits its new parent's ACL.
/// </summary>
public sealed partial class ResticRestoreDatabaseTests
{
    private async Task<(ShareDefinition Share, Group Group, string Root)> AclWorldAsync()
    {
        var root = Directory.CreateTempSubdirectory("kaimo-acl-always-").FullName;
        var share = new ShareDefinition("projects", root);
        var group = new Group(Guid.NewGuid(), "Board");
        await using var db = NewContext();
        db.Users.Add(_user);
        db.Groups.Add(group);
        db.ShareDefinitions.Add(share);
        await db.SaveChangesAsync();
        Directory.CreateDirectory(Path.Combine(root, "Restored"));
        File.WriteAllText(Path.Combine(root, "Restored", "secret.txt"), "x");
        return (share, group, root);
    }

    private static BackupSnapshot Snapshot(bool withManifest) => new()
    {
        SnapshotId = "snap",
        Kind = BackupSnapshotKind.Share,
        PathsJson = withManifest
            ? "[\"/data/storage/pool01/projects\",\"/meta/share-x/kaimo-acl-manifest.json\"]"
            : "[\"/data/storage/pool01/projects\"]",
    };

    private static Func<string, Stream, CancellationToken, Task> Dump(string manifest)
        => (_, output, token) => output.WriteAsync(Encoding.UTF8.GetBytes(manifest), token).AsTask();

    [Fact]
    public async Task ApplyBackedUpPermissions_KeepsARestrictiveAclOnTheRestoredCopy()
    {
        var (share, group, root) = await AclWorldAsync();
        try
        {
            var manifest = $$"""
                {"formatVersion":1,"exportedAtUtc":"2026-10-09T00:00:00Z","share":{"id":"{{share.Id}}"},"entries":[
                  {"path":"docs/secret.txt","isDirectory":false,"ownerId":"{{_user.Id}}","ownerName":"alice","acl":[
                    {"principalId":"{{group.Id}}","principalName":"Board","entryType":0,"permissions":1,"inheritance":0}]}]}
                """;
            var outcome = new BackupRestoreOutcome();

            var document = await BackupRestoreExecutor.ReadManifestAsync(Snapshot(withManifest: true), Dump(manifest),
                Path.Combine(root, ".staging"), CancellationToken.None);
            await BackupRestoreExecutor.ApplyBackedUpPermissionsAsync(DbFactory, document, share.Id, root,
                p => BackupRestoreExecutor.MapPath(p, "docs", new HashSet<string>(), "Restored", []),
                _user.Id, DateTime.UtcNow, outcome, CancellationToken.None);

            Assert.Equal(1, outcome.AclEntriesApplied);
            await using var check = NewContext();
            var row = await check.FileMetadata.Include(m => m.Acl).SingleAsync(m => m.ShareId == share.Id);
            Assert.Equal("Restored/secret.txt", row.Path);
            Assert.Equal(group.Id, Assert.Single(row.Acl).PrincipalId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadManifest_WithoutManifest_RefusesTheRestore()
    {
        var dumped = false;

        var error = await Assert.ThrowsAsync<ResticException>(() => BackupRestoreExecutor.ReadManifestAsync(
            Snapshot(withManifest: false), (_, _, _) => { dumped = true; return Task.CompletedTask; },
            Path.Combine(Path.GetTempPath(), "kaimo-no-manifest-" + Guid.NewGuid().ToString("N")), CancellationToken.None));

        Assert.Equal("manifest_missing", error.Code);
        Assert.False(dumped);
    }

    [Fact]
    public async Task ReadManifest_Unreadable_RefusesTheRestore()
    {
        var staging = Path.Combine(Path.GetTempPath(), "kaimo-bad-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = await Assert.ThrowsAsync<ResticException>(() => BackupRestoreExecutor.ReadManifestAsync(
                Snapshot(withManifest: true), Dump("""{"formatVersion":99,"entries":[]}"""), staging, CancellationToken.None));

            Assert.Equal("manifest_invalid", error.Code);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }
}
