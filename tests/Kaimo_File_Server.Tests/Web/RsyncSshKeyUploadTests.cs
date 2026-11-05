using System.Text;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

// ssh-keygen 9.x in the Linux container rejects keys with CRLF, a UTF-8 BOM or a missing
// final newline ("error in libcrypto"); the upload canonicalizes them before validation.
public sealed class RsyncSshKeyUploadTests : IDisposable
{
    private const string Body =
        "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nAAAABG5vbmUAAAAEbm9uZQ==\n-----END OPENSSH PRIVATE KEY-----\n";

    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(), $"kaimo-ssh-upload-tests-{Guid.NewGuid():N}");

    public RsyncSshKeyUploadTests() => Directory.CreateDirectory(_temporaryRoot);

    public void Dispose() => Directory.Delete(_temporaryRoot, recursive: true);

    [Theory]
    [InlineData("canonical", Body)]
    [InlineData("crlf", "-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAA\r\nAAAABG5vbmUAAAAEbm9uZQ==\r\n-----END OPENSSH PRIVATE KEY-----\r\n")]
    [InlineData("lone cr", "-----BEGIN OPENSSH PRIVATE KEY-----\rb3BlbnNzaC1rZXktdjEAAAAA\rAAAABG5vbmUAAAAEbm9uZQ==\r-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("no final newline", "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nAAAABG5vbmUAAAAEbm9uZQ==\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("bom + padding", "﻿  \n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nAAAABG5vbmUAAAAEbm9uZQ==\n-----END OPENSSH PRIVATE KEY-----\n\n  \r\n")]
    public void NormalizePrivateKey_ProducesOpenSshCanonicalForm(string variant, string uploaded)
    {
        byte[] normalized = RsyncSshSetupService.NormalizePrivateKey(Encoding.UTF8.GetBytes(uploaded));

        Assert.True(Body == Encoding.ASCII.GetString(normalized), variant);
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA== user@host\n")]
    [InlineData("﻿ecdsa-sha2-nistp256 AAAAE2VjZHNh user@host\r\n")]
    [InlineData("sk-ssh-ed25519@openssh.com AAAAGnNrLXNzaA== user@host")]
    public async Task ValidatePrivateKey_PublicKeyFile_ReportsDedicatedError(string uploaded)
    {
        var service = new RsyncSshSetupService(_temporaryRoot);

        var exception = await Assert.ThrowsAsync<ProtocolConfigurationException>(
            () => service.ValidatePrivateKeyAsync(Encoding.UTF8.GetBytes(uploaded)));

        Assert.Equal("private_key_is_public", exception.Code);
    }

    [Theory]
    [InlineData(RsyncSshSetupService.RsyncKeyComment, "kaimo-file-server-rsync")]
    [InlineData(RsyncSshSetupService.SftpKeyComment, "kaimo-file-server-sftp")]
    public async Task GeneratePrivateKey_PublicKeyCarriesProtocolComment(string comment, string expected)
    {
        // Windows OpenSSH refuses "ssh-keygen -y" on the temporary key file (its ACLs are not
        // restricted like the Unix 0600 mode); the service only runs in the Linux container.
        if (OperatingSystem.IsWindows())
            return;
        var service = new RsyncSshSetupService(_temporaryRoot);

        RsyncSshKeyMaterial material = await service.GeneratePrivateKeyAsync(comment);

        Assert.EndsWith(" " + expected, material.PublicKey);
    }

    [Theory]
    [InlineData(" \r\n\t\n")]
    [InlineData("﻿")]
    public async Task ValidatePrivateKey_WhitespaceOnly_IsInvalid(string uploaded)
    {
        var service = new RsyncSshSetupService(_temporaryRoot);

        var exception = await Assert.ThrowsAsync<ProtocolConfigurationException>(
            () => service.ValidatePrivateKeyAsync(Encoding.UTF8.GetBytes(uploaded)));

        Assert.Equal("private_key_invalid", exception.Code);
    }
}
