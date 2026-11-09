using System.Text;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage;

/// <summary>
/// A sync pull streams smbclient's output into the target instead of spooling the file to the
/// container's temporary directory, which is usually far smaller than the storage pool.
/// </summary>
public sealed class SmbStreamingDownloadTests
{
    private readonly Mock<IProtocolCommandRunner> _runner = new();
    private readonly IRemoteFileStore _store;

    public SmbStreamingDownloadTests()
        => _store = new SmbCommandRemoteFileStore(
            new SmbConnectionSettings("files.example.test"),
            new SmbCredentials("user", "secret", null),
            _runner.Object);

    private void HelperWrites(string content, Action<IReadOnlyList<string>>? inspect = null)
        => _runner.Setup(r => r.StreamOutputAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> arguments, Stream target, CancellationToken token) =>
            {
                inspect?.Invoke(arguments);
                return target.WriteAsync(Encoding.UTF8.GetBytes(content), token).AsTask();
            });

    [Fact]
    public async Task DownloadTo_StreamsTheRemoteFileIntoTheTarget()
    {
        HelperWrites("video-bytes");
        using var target = new MemoryStream();

        await _store.DownloadToAsync("/Team/Folder/big.mp4", target);

        Assert.Equal("video-bytes", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task DownloadTo_AsksSmbclientToWriteToStandardOutput()
    {
        IReadOnlyList<string>? arguments = null;
        HelperWrites("x", received => arguments = received);

        await _store.DownloadToAsync("/Team/Schule und Beruf/Projektpräsentation/Remuxen.mp4", new MemoryStream());

        Assert.Equal("//files.example.test/Team", arguments![1]);
        Assert.Equal("get \"Schule und Beruf\\Projektpräsentation\\Remuxen.mp4\" -", arguments[^1]);
        Assert.Contains("--client-protection=encrypt", arguments);
    }

    [Fact]
    public async Task DownloadTo_KeepsTheCredentialFileForTheWholeTransferAndRemovesItAfterwards()
    {
        string? authFile = null;
        bool existedDuringTransfer = false;
        HelperWrites("x", arguments =>
        {
            authFile = arguments[arguments.ToList().IndexOf("-A") + 1];
            existedDuringTransfer = File.Exists(authFile);
        });

        await _store.DownloadToAsync("/Team/a.bin", new MemoryStream());

        Assert.True(existedDuringTransfer);
        Assert.False(File.Exists(authFile));
    }

    [Fact]
    public async Task DownloadTo_DoesNotBufferThroughTheOutputCapturingRun()
    {
        HelperWrites("x");

        await _store.DownloadToAsync("/Team/a.bin", new MemoryStream());

        _runner.Verify(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task DownloadTo_HelperFailure_Propagates()
    {
        _runner.Setup(r => r.StreamOutputAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The protocol helper failed with exit code 1: NT_STATUS_IO_TIMEOUT"));

        await Assert.ThrowsAsync<IOException>(() => _store.DownloadToAsync("/Team/a.bin", new MemoryStream()));
    }

    [Theory]
    [InlineData("/Team")]
    [InlineData("/Team/../secret.bin")]
    public async Task DownloadTo_InvalidPath_IsRejectedWithoutRunningTheHelper(string path)
    {
        await Assert.ThrowsAsync<ProtocolConfigurationException>(() => _store.DownloadToAsync(path, new MemoryStream()));

        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncAdapter_DownloadsThroughTheStreamingPath()
    {
        HelperWrites("streamed");
        ICloudConnection adapter = new RemoteFileStoreSyncAdapter("SMB", _store);
        using var target = new MemoryStream();

        await adapter.DownloadAsync("/Team/a.bin", target);

        Assert.Equal("streamed", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task DownloadTo_StoreWithoutStreaming_FallsBackToOpenRead()
    {
        IRemoteFileStore store = new ReadOnlyStore("from-open-read");
        using var target = new MemoryStream();

        await store.DownloadToAsync("/a.bin", target);

        Assert.Equal("from-open-read", Encoding.UTF8.GetString(target.ToArray()));
    }

    private sealed class ReadOnlyStore(string content) : IRemoteFileStore
    {
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));

        public Task<IReadOnlyList<RemoteStorageItem>> ListAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task WriteAsync(string path, Stream data, bool overwrite, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
