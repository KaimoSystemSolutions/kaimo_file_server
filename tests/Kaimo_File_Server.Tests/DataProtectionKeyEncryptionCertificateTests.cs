using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class DataProtectionKeyEncryptionCertificateTests
{
    [Fact]
    public void LoadOrCreate_PersistsAndReusesPrivateCertificate()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-dp-cert-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = DataProtectionKeyEncryptionCertificate.LoadOrCreate(root);
            using var second = DataProtectionKeyEncryptionCertificate.LoadOrCreate(root);

            Assert.True(first.HasPrivateKey);
            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.Single(Directory.GetFiles(
                Path.Combine(root, ".dp-certificate"),
                "key-encryption.pfx"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
