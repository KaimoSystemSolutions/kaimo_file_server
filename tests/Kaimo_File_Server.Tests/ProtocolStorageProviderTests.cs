using System.Security.Cryptography;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class ProtocolStorageProviderTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(), $"kaimo-protocol-tests-{Guid.NewGuid():N}");

    public ProtocolStorageProviderTests() => Directory.CreateDirectory(_temporaryRoot);

    [Fact]
    public void Catalog_RejectsDuplicateProviderIdsIgnoringCase()
    {
        var first = new Mock<IStorageConnectionProvider>();
        first.SetupGet(provider => provider.Id).Returns("SMB");
        var second = new Mock<IStorageConnectionProvider>();
        second.SetupGet(provider => provider.Id).Returns("smb");

        Assert.Throws<ArgumentException>(() =>
            new StorageConnectionProviderCatalog([first.Object, second.Object]));
    }

    [Fact]
    public async Task SmbProvider_RequiresStrongTransportAndVerifiedOperatorMount()
    {
        string mount = CreateDirectory("smb-mount");
        string attestation = Path.Combine(_temporaryRoot, "smb-attestation.json");
        var provider = new SmbStorageConnectionProvider();
        var unsafeConnection = Connection(
            "smb",
            StorageAuthorizationMode.HostMount,
            new SmbMountConnectionSettings(
                "files.example.test", "x", "certificate-sha256:abc", mount, attestation,
                RequireEncryption: false));

        var unsafeResult = await provider.TestAsync(unsafeConnection);

        Assert.Equal(StorageConnectionHealthState.InvalidConfiguration, unsafeResult.State);
        Assert.Equal("transport_policy_unsafe", unsafeResult.Code);

        WriteAttestation(attestation, new ProtocolMountAttestation(
            "smb", mount, "//files.example.test/x", "certificate-sha256:abc",
            ["operator-managed", "smb3", "signing", "encryption"], DateTime.UtcNow.AddMinutes(5)));
        var connection = Connection(
            "smb",
            StorageAuthorizationMode.HostMount,
            new SmbMountConnectionSettings(
                "files.example.test", "x", "certificate-sha256:abc", mount, attestation));

        await using var session = await provider.OpenSessionAsync(connection);

        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.Browse));
        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.RequiresHostMount));
        Assert.NotNull(session.RemoteFiles);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            session.RemoteFiles!.ListAsync("../outside"));
    }

    [Fact]
    public async Task SmbProvider_FailsClosedWhenAttestedIdentityDoesNotMatch()
    {
        string mount = CreateDirectory("identity-mount");
        string attestation = Path.Combine(_temporaryRoot, "identity-attestation.json");
        WriteAttestation(attestation, new ProtocolMountAttestation(
            "smb", mount, "//files.example.test/share", "certificate-sha256:unexpected",
            ["operator-managed", "smb3", "signing", "encryption"], DateTime.UtcNow.AddMinutes(5)));
        var connection = Connection(
            "smb",
            StorageAuthorizationMode.HostMount,
            new SmbMountConnectionSettings(
                "files.example.test", "share", "certificate-sha256:expected", mount, attestation));

        var result = await new SmbStorageConnectionProvider().TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.IdentityMismatch, result.State);
        Assert.Equal("identity_mismatch", result.Code);
    }

    [Fact]
    public async Task NfsProvider_RejectsLegacyProtocolVersions()
    {
        var connection = Connection(
            "nfs",
            StorageAuthorizationMode.HostMount,
            new NfsMountConnectionSettings(
                "nfs.example.test", "/exports/data", "host-key:abc",
                CreateDirectory("nfs-mount"), Path.Combine(_temporaryRoot, "nfs-attestation.json"),
                MinimumMajorVersion: 3));

        var result = await new NfsStorageConnectionProvider().TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.InvalidConfiguration, result.State);
        Assert.Equal("nfs_version_unsafe", result.Code);
    }

    [Fact]
    public async Task RsyncSshProvider_RequiresPinnedKnownHostAndExposesNoBrowseContract()
    {
        string privateKey = WriteFile("id_ed25519", "private-key-placeholder");
        byte[] publicKey = RandomNumberGenerator.GetBytes(32);
        string knownHosts = WriteFile(
            "known_hosts",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(publicKey)}");
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
        var runner = new Mock<IRsyncProcessRunner>();
        runner.Setup(candidate => candidate.TestSshAsync(
                It.IsAny<RsyncSshConnectionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var provider = new RsyncSshStorageConnectionProvider(runner.Object);
        var connection = Connection(
            "rsync-ssh",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", fingerprint,
                privateKey, knownHosts));

        await using var session = await provider.OpenSessionAsync(connection);
        var health = await provider.TestAsync(connection);

        Assert.True(health.IsHealthy);
        Assert.True(session.Capabilities.HasFlag(StorageProviderCapabilities.OptimizedSync));
        Assert.False(session.Capabilities.HasFlag(StorageProviderCapabilities.Browse));
        Assert.Null(session.RemoteFiles);
        Assert.NotNull(session.OptimizedSync);
    }

    [Fact]
    public async Task RsyncSshProvider_ReportsPinnedHostMismatchAsIdentityFailure()
    {
        string privateKey = WriteFile("mismatch-key", "private-key-placeholder");
        string knownHosts = WriteFile(
            "mismatch-known-hosts",
            $"backup.example.test ssh-ed25519 {Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");
        string differentFingerprint = "SHA256:"
                                      + Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)))
                                          .TrimEnd('=');
        var connection = Connection(
            "rsync-ssh",
            StorageAuthorizationMode.SshKey,
            new RsyncSshConnectionSettings(
                "backup.example.test", 22, "backup", "/srv/archive", differentFingerprint,
                privateKey, knownHosts));

        var result = await new RsyncSshStorageConnectionProvider(Mock.Of<IRsyncProcessRunner>())
            .TestAsync(connection);

        Assert.Equal(StorageConnectionHealthState.IdentityMismatch, result.State);
        Assert.Equal("host_key_mismatch", result.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    private string CreateDirectory(string name)
    {
        string path = Path.Combine(_temporaryRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_temporaryRoot, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static void WriteAttestation(string path, ProtocolMountAttestation attestation)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(attestation));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    private static StorageConnection Connection<T>(
        string providerId,
        StorageAuthorizationMode authorizationMode,
        T settings) => new()
    {
        ProviderId = providerId,
        AuthorizationMode = authorizationMode,
        SettingsJson = JsonSerializer.Serialize(settings),
        State = StorageConnectionState.Ready
    };
}
