using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The key-encryption certificate can be supplied from outside the application data
/// (DataProtection:CertificatePath), so a copy of the data volume alone no longer holds
/// both the key ring and the key that decrypts it.
/// </summary>
public sealed class DataProtectionCertificateLocationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kaimo-dp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadFromFile_MissingFile_FailsInsteadOfCreatingOne()
    {
        var path = Path.Combine(_directory, "missing.pfx");

        Assert.Throws<FileNotFoundException>(() => DataProtectionKeyEncryptionCertificate.LoadFromFile(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LoadFromFile_MovedCertificate_LoadsTheSameKey()
    {
        using var generated = DataProtectionKeyEncryptionCertificate.LoadOrCreate(_directory);
        var moved = Path.Combine(_directory, "secret.pfx");
        File.Move(Path.Combine(_directory, ".dp-certificate", "key-encryption.pfx"), moved);

        using var loaded = DataProtectionKeyEncryptionCertificate.LoadFromFile(moved);

        Assert.Equal(generated.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
