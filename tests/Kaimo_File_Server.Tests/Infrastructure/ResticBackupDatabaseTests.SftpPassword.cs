using Xunit;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupDatabaseTests
{
    [Fact]
    public async Task WriteAskpass_HelperPrintsThePrivatePasswordFile()
    {
        const string sshPassword = "s3cret pass'word";
        var dir = Path.Combine(Path.GetTempPath(), "kaimo-askpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var askpass = await ResticTargetResolver.WriteAskpassAsync(dir, sshPassword, CancellationToken.None);

            var passwordFile = Path.Combine(dir, "ssh-password");
            Assert.Equal(sshPassword, await File.ReadAllTextAsync(passwordFile));
            // The password itself is never part of the script; quotes in it cannot break out.
            var script = await File.ReadAllTextAsync(askpass);
            Assert.Equal($"#!/bin/sh\nexec /bin/cat '{passwordFile}'\n", script);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(askpass));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(passwordFile));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_SftpPasswordConnection_FeedsThePasswordThroughAskpass_NeverAsArgument()
    {
        if (OperatingSystem.IsWindows())
            return; // The ssh command line needs shell-neutral POSIX paths; the production container is Linux.

        const string sshPassword = "s3cret pass'word";
        var root = Path.Combine(Path.GetTempPath(), "kaimo-sftp-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hostKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(51);
            var fingerprint = PinnedSshSettings.ComputeFingerprint(hostKey);
            var knownHosts = Path.Combine(root, "known_hosts");
            await File.WriteAllTextAsync(knownHosts, $"nas.example.test ssh-ed25519 {Convert.ToBase64String(hostKey)}\n");

            var connection = new StorageConnection
            {
                ProviderId = "sftp",
                Name = "NAS",
                AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
                State = StorageConnectionState.Ready,
                EncryptedCredentialPayload = "protected-credentials",
                SettingsJson = JsonSerializer.Serialize(new RsyncSshConnectionSettings(
                    "nas.example.test", 22, "backup", "/volume1", fingerprint, null, knownHosts)),
            };
            var repo = new BackupRepository
            {
                Name = "Offsite",
                Backend = BackupBackend.Sftp,
                State = BackupRepositoryState.Active,
                EncryptedPassword = "protected-password",
                SettingsJson = new ResticRepositorySettings { Path = "kaimo", StorageConnectionId = connection.Id }.Serialize(),
            };
            await using (var db = NewContext())
            {
                db.StorageConnections.Add(connection);
                db.BackupRepositories.Add(repo);
                await db.SaveChangesAsync();
            }

            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.Unprotect<string>("protected-password", It.IsAny<CredentialContext>())).Returns("repo-pw");
            vault.Setup(v => v.Unprotect<Dictionary<string, string>>("protected-credentials", It.IsAny<CredentialContext>()))
                .Returns(new Dictionary<string, string> { ["username"] = "backup", ["password"] = sshPassword });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            }).Build();
            var resolver = new ResticTargetResolver(DbFactory, vault.Object, config);

            string askpass;
            using (var target = await resolver.ResolveAsync(repo, CancellationToken.None))
            {
                Assert.Equal("sftp:backup@nas.example.test:/volume1/kaimo", target.RepositoryUrl);
                Assert.Equal("force", target.Environment["SSH_ASKPASS_REQUIRE"]);
                askpass = target.Environment["SSH_ASKPASS"];
                Assert.StartsWith(Path.Combine(root, "tmp"), askpass);

                var command = Assert.Single(target.GlobalArguments, a => a.StartsWith("sftp.command=", StringComparison.Ordinal));
                Assert.Contains("PubkeyAuthentication=no", command);
                Assert.DoesNotContain(",", command);
                Assert.DoesNotContain(" -i ", command);
                // The password never appears in arguments or the environment, only in the private file.
                Assert.DoesNotContain(target.GlobalArguments, a => a.Contains(sshPassword, StringComparison.Ordinal));
                Assert.DoesNotContain(target.Environment.Values, v => v.Contains(sshPassword, StringComparison.Ordinal));

                Assert.Equal(sshPassword, await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(askpass)!, "ssh-password")));
            }
            Assert.False(File.Exists(askpass));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_SftpPasswordConnectionWithoutPassword_FailsWithStableCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-sftp-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hostKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(51);
            var knownHosts = Path.Combine(root, "known_hosts");
            await File.WriteAllTextAsync(knownHosts, $"nas.example.test ssh-ed25519 {Convert.ToBase64String(hostKey)}\n");
            var connection = new StorageConnection
            {
                ProviderId = "sftp",
                Name = "NAS",
                AuthorizationMode = StorageAuthorizationMode.UsernamePassword,
                SettingsJson = JsonSerializer.Serialize(new RsyncSshConnectionSettings(
                    "nas.example.test", 22, "backup", "/volume1", PinnedSshSettings.ComputeFingerprint(hostKey), null, knownHosts)),
            };
            var repo = new BackupRepository
            {
                Name = "Offsite",
                Backend = BackupBackend.Sftp,
                EncryptedPassword = "protected-password",
                SettingsJson = new ResticRepositorySettings { Path = "kaimo", StorageConnectionId = connection.Id }.Serialize(),
            };
            await using (var db = NewContext())
            {
                db.StorageConnections.Add(connection);
                db.BackupRepositories.Add(repo);
                await db.SaveChangesAsync();
            }
            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.Unprotect<string>("protected-password", It.IsAny<CredentialContext>())).Returns("repo-pw");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Restic:TempDirectory"] = Path.Combine(root, "tmp"),
            }).Build();

            var exception = await Assert.ThrowsAsync<ResticException>(() =>
                new ResticTargetResolver(DbFactory, vault.Object, config).ResolveAsync(repo, CancellationToken.None));

            Assert.Equal("ssh_password_missing", exception.Code);
            // No helper or password file is left behind.
            Assert.False(Directory.Exists(Path.Combine(root, "tmp")) && Directory.EnumerateFiles(Path.Combine(root, "tmp"), "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
