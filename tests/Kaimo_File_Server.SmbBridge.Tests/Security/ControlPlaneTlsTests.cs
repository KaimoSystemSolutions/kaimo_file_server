using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kaimo_File_Server.SmbBridge.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kaimo_File_Server.SmbBridge.Tests.Security;

/// <summary>
/// Pins the bridge's wait for its control-plane certificates. The Host generates
/// the PKI on its own startup, so the bridge may start before the files exist (or
/// while they are still being written) and must wait instead of crashing.
/// </summary>
public sealed class ControlPlaneTlsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("kaimo-control-plane-tls");

    private string CertificatePath => Path.Combine(_directory.FullName, "server.crt");
    private string KeyPath => Path.Combine(_directory.FullName, "server.key");
    private string CaPath => Path.Combine(_directory.FullName, "ca.crt");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Load_FilesPresent_LoadsServerIdentityWithPrivateKey()
    {
        WriteIdentity();

        using var tls = ControlPlaneTls.Load(Configuration(), TimeSpan.Zero);

        Assert.True(tls.ServerCertificate.HasPrivateKey);
    }

    [Fact]
    public async Task Load_FilesAppearLater_WaitsAndLoads()
    {
        var writer = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            WriteIdentity();
        });

        using var tls = ControlPlaneTls.Load(Configuration(), TimeSpan.FromSeconds(20));
        await writer;

        Assert.True(tls.ServerCertificate.HasPrivateKey);
    }

    [Fact]
    public async Task Load_FilesStillBeingWritten_RetriesUntilComplete()
    {
        // The generator creates the key before the certificate is signed: an
        // empty certificate file must be retried, not treated as fatal.
        File.WriteAllText(CertificatePath, string.Empty);
        File.WriteAllText(KeyPath, string.Empty);
        File.WriteAllText(CaPath, string.Empty);
        var writer = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            WriteIdentity();
        });

        using var tls = ControlPlaneTls.Load(Configuration(), TimeSpan.FromSeconds(20));
        await writer;

        Assert.True(tls.ServerCertificate.HasPrivateKey);
    }

    [Fact]
    public void Load_FilesNeverAppear_ThrowsFileNotFoundAfterWait()
    {
        Assert.Throws<FileNotFoundException>(
            () => ControlPlaneTls.Load(Configuration(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Load_PathNotConfigured_ThrowsImmediately()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:Tls:ServerKeyPath"] = KeyPath,
                ["ControlPlane:Tls:ClientCaCertificatePath"] = CaPath,
            })
            .Build();
        var started = DateTime.UtcNow;

        Assert.Throws<InvalidOperationException>(
            () => ControlPlaneTls.Load(configuration, TimeSpan.FromSeconds(20)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5),
            "A configuration error must not be retried.");
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControlPlane:Tls:ServerCertificatePath"] = CertificatePath,
            ["ControlPlane:Tls:ServerKeyPath"] = KeyPath,
            ["ControlPlane:Tls:ClientCaCertificatePath"] = CaPath,
        })
        .Build();

    private void WriteIdentity()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=kaimo_smb_bridge", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string pem = certificate.ExportCertificatePem();

        File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(CaPath, pem);
        File.WriteAllText(CertificatePath, pem);
    }
}
