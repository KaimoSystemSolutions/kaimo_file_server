using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services.Https;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the HTTPS certificate feature: the <see cref="HttpsCertificateSettings"/> model
/// (defaults + normalization) and the <see cref="HttpsCertificateProvider"/> generation,
/// renewal-window, custom-import and persistence behaviour.
/// </summary>
public class HttpsCertificateProviderTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // ─────────────────────────── Settings model ───────────────────────────

    [Fact]
    public void Default_IsAutoThreeMonths()
    {
        var s = HttpsCertificateSettings.Default();
        Assert.Equal(CertificateMode.Auto, s.Mode);
        Assert.Equal(90, s.LifetimeDays);
        Assert.Equal(21, s.RenewalLeadDays);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(45, 30)]   // <= midpoint (60) snaps down to one month
    [InlineData(61, 90)]   // > midpoint snaps up to three months
    [InlineData(90, 90)]
    [InlineData(365, 90)]
    public void Normalize_SnapsLifetimeToSupportedValue(int input, int expected)
    {
        var s = new HttpsCertificateSettings { LifetimeDays = input };
        s.Normalize();
        Assert.Equal(expected, s.LifetimeDays);
    }

    [Fact]
    public void Normalize_ClampsRenewalLeadToHalfLifetime()
    {
        var s = new HttpsCertificateSettings { LifetimeDays = 30, RenewalLeadDays = 100 };
        s.Normalize();
        Assert.Equal(15, s.RenewalLeadDays); // max lead = lifetime/2
    }

    [Fact]
    public void Normalize_TrimsAndDeduplicatesSansAndCommonName()
    {
        var s = new HttpsCertificateSettings
        {
            CommonName = "   ",
            AdditionalSans = ["  a ", "a", "", "B"],
        };
        s.Normalize();

        Assert.Null(s.CommonName);
        Assert.Equal(new[] { "a", "B" }, s.AdditionalSans);
    }

    // ─────────────────────────── Generation ───────────────────────────

    [Fact]
    public void GenerateSelfSigned_HasExpectedLifetimeAndSans()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var settings = new HttpsCertificateSettings { LifetimeDays = 90 };

        var (cert, _) = provider.GenerateSelfSigned(settings);

        var lifetimeDays = (cert.NotAfter - cert.NotBefore).TotalDays;
        Assert.InRange(lifetimeDays, 89.9, 90.1);
        Assert.True(cert.HasPrivateKey);

        var san = ReadSan(cert);
        Assert.Contains("testhost", san);
        Assert.Contains("localhost", san);
        Assert.Contains("127.0.0.1", san);
        Assert.Contains("10.1.2.3", san); // from the stubbed network address
    }

    // ─────────────────────────── Initialization + renewal ───────────────────────────

    [Fact]
    public async Task InitializeAsync_GeneratesAndPersistsWhenNoneExists()
    {
        var (provider, _, dir, _) = NewProvider(DateTimeOffset.UtcNow);

        await provider.InitializeAsync();

        Assert.NotNull(provider.Current);
        Assert.True(File.Exists(Path.Combine(dir, ".certs", "current.pfx.protected")));
    }

    [Fact]
    public async Task EnsureValidAsync_RenewsInsideWindow_NotOutside()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Outside the window (remaining 30d > 21d lead): no renewal.
        var (p1, _, _, time1) = NewProvider(t0);
        await p1.InitializeAsync();
        var thumb1 = p1.Current!.Thumbprint;
        time1.Now = t0.AddDays(60);
        Assert.False(await p1.EnsureValidAsync());
        Assert.Equal(thumb1, p1.Current!.Thumbprint);

        // Inside the window (remaining 20d < 21d lead): renews to a new certificate.
        var (p2, _, _, time2) = NewProvider(t0);
        await p2.InitializeAsync();
        var thumb2 = p2.Current!.Thumbprint;
        time2.Now = t0.AddDays(70);
        Assert.True(await p2.EnsureValidAsync());
        Assert.NotEqual(thumb2, p2.Current!.Thumbprint);
    }

    [Fact]
    public async Task EnsureValidAsync_CustomMode_NeverRenews()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (provider, cfg, _, time) = NewProvider(t0);

        // Import a custom cert → mode becomes Custom.
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings { LifetimeDays = 30 });
        await provider.ImportCustomAsync(cert.Export(X509ContentType.Pfx, "pw"), "pw");
        var thumb = provider.Current!.Thumbprint;

        // Even long past the renewal window, custom certs are left untouched.
        time.Now = t0.AddDays(29);
        Assert.False(await provider.EnsureValidAsync());
        Assert.Equal(thumb, provider.Current!.Thumbprint);
        Assert.Equal(CertificateMode.Custom, cfg.Get<HttpsCertificateSettings>().Mode);
    }

    // ─────────────────────────── Custom import ───────────────────────────

    [Fact]
    public async Task ImportCustomAsync_ValidPfx_IsAccepted()
    {
        var (provider, cfg, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());

        await provider.ImportCustomAsync(cert.Export(X509ContentType.Pfx, "pw"), "pw");

        Assert.NotNull(provider.Current);
        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(CertificateMode.Custom, cfg.Get<HttpsCertificateSettings>().Mode);
    }

    [Fact]
    public async Task ImportCustomAsync_Garbage_Throws()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync([1, 2, 3, 4], null));
    }

    [Fact]
    public async Task ImportCustomAsync_PublicOnlyPfx_IsRejected()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());

        // A PFX built from the public certificate has no private key.
        var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var pfxWithoutKey = publicOnly.Export(X509ContentType.Pfx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync(pfxWithoutKey, null));
    }

    [Fact]
    public async Task ImportCustomAsync_PemWithUnencryptedKey_IsAccepted()
    {
        var (provider, cfg, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        // A concatenated cert + PKCS#8 key bundle, as produced by e.g. Let's Encrypt tooling.
        var bundle = cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem();

        await provider.ImportCustomAsync(Encoding.ASCII.GetBytes(bundle), null);

        Assert.NotNull(provider.Current);
        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(CertificateMode.Custom, cfg.Get<HttpsCertificateSettings>().Mode);
    }

    [Fact]
    public async Task ImportCustomAsync_EncryptedPem_WithPassword_IsAccepted()
    {
        var (provider, cfg, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var bundle = cert.ExportCertificatePem() + "\n"
            + rsa.ExportEncryptedPkcs8PrivateKeyPem("s3cret".AsSpan(), pbe);

        await provider.ImportCustomAsync(Encoding.ASCII.GetBytes(bundle), "s3cret");

        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(CertificateMode.Custom, cfg.Get<HttpsCertificateSettings>().Mode);
    }

    [Fact]
    public async Task ImportCustomAsync_EncryptedPem_WrongPassword_Throws()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var bundle = cert.ExportCertificatePem() + "\n"
            + rsa.ExportEncryptedPkcs8PrivateKeyPem("s3cret".AsSpan(), pbe);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync(Encoding.ASCII.GetBytes(bundle), "wrong"));
    }

    [Fact]
    public async Task ImportCustomAsync_PublicOnlyPem_IsRejected()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());

        // Certificate PEM only — no private key block. Cannot serve HTTPS.
        var certOnly = Encoding.ASCII.GetBytes(cert.ExportCertificatePem());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync(certOnly, null));
    }

    // ─────────────────── Separate certificate + key files ───────────────────

    [Fact]
    public async Task ImportCustomAsync_SeparatePemCertAndPemKey_IsAccepted()
    {
        var (provider, cfg, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var certFile = Encoding.ASCII.GetBytes(cert.ExportCertificatePem());
        var keyFile = Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem());

        await provider.ImportCustomAsync(certFile, null, keyFile);

        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, provider.Current!.Thumbprint);
        Assert.Equal(CertificateMode.Custom, cfg.Get<HttpsCertificateSettings>().Mode);
    }

    [Fact]
    public async Task ImportCustomAsync_DerCertAndPemKey_IsAccepted()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var certFile = cert.Export(X509ContentType.Cert);                 // DER .crt
        var keyFile = Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()); // PEM key

        await provider.ImportCustomAsync(certFile, null, keyFile);

        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, provider.Current!.Thumbprint);
    }

    [Fact]
    public async Task ImportCustomAsync_PemCertAndDerKey_IsAccepted()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var certFile = Encoding.ASCII.GetBytes(cert.ExportCertificatePem());
        var keyFile = rsa.ExportPkcs8PrivateKey();  // raw DER PKCS#8 (.key/.der)

        await provider.ImportCustomAsync(certFile, null, keyFile);

        Assert.True(provider.Current!.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, provider.Current!.Thumbprint);
    }

    [Fact]
    public async Task ImportCustomAsync_SeparateEncryptedKey_WithPassword_IsAccepted()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());
        using var rsa = cert.GetRSAPrivateKey()!;

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var certFile = Encoding.ASCII.GetBytes(cert.ExportCertificatePem());
        var keyFile = Encoding.ASCII.GetBytes(rsa.ExportEncryptedPkcs8PrivateKeyPem("pw".AsSpan(), pbe));

        await provider.ImportCustomAsync(certFile, "pw", keyFile);

        Assert.True(provider.Current!.HasPrivateKey);
    }

    [Fact]
    public async Task ImportCustomAsync_SeparateKey_ThatIsNotAKey_Throws()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());

        var certFile = Encoding.ASCII.GetBytes(cert.ExportCertificatePem());
        var notAKey = new byte[] { 1, 2, 3, 4, 5 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync(certFile, null, notAKey));
    }

    [Fact]
    public async Task ImportCustomAsync_SecondImport_ReplacesCurrentAndPersisted()
    {
        var (provider, cfg, dir, _) = NewProvider(DateTimeOffset.UtcNow);

        // First upload.
        var (certA, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings { CommonName = "cert-a" });
        await provider.ImportCustomAsync(certA.Export(X509ContentType.Pfx, "pw"), "pw");
        var thumbA = provider.Current!.Thumbprint;

        // Second upload replaces the served certificate in memory…
        var (certB, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings { CommonName = "cert-b" });
        await provider.ImportCustomAsync(certB.Export(X509ContentType.Pfx, "pw"), "pw");
        var thumbB = provider.Current!.Thumbprint;

        Assert.NotEqual(thumbA, thumbB);
        Assert.Equal(certB.Thumbprint, thumbB);

        // …and on disk, so it also survives a restart (fresh provider over the same store).
        var reloaded = new HttpsCertificateProvider(
            NewScopeFactory(cfg), NewSysInfo(), new PassthroughProtection(),
            new TestTime(DateTimeOffset.UtcNow), dir, NullLogger<HttpsCertificateProvider>.Instance);
        await reloaded.InitializeAsync();
        Assert.Equal(thumbB, reloaded.Current!.Thumbprint);
    }

    [Fact]
    public async Task ImportCustomAsync_BareDerCertificate_IsRejected()
    {
        var (provider, _, _, _) = NewProvider(DateTimeOffset.UtcNow);
        var (cert, _) = provider.GenerateSelfSigned(new HttpsCertificateSettings());

        // A raw DER .crt/.cer file: valid certificate, but no private key.
        var der = cert.Export(X509ContentType.Cert);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImportCustomAsync(der, null));
    }

    // ─────────────────────────── Persistence ───────────────────────────

    [Fact]
    public async Task InitializeAsync_ReloadsPersistedCertificate()
    {
        var dir = NewTempDir();
        var protection = new PassthroughProtection();
        var scopeFactory = NewScopeFactory(new FakeConfig());
        var sys = NewSysInfo();
        var time = new TestTime(DateTimeOffset.UtcNow);

        var p1 = new HttpsCertificateProvider(
            scopeFactory, sys, protection, time, dir, NullLogger<HttpsCertificateProvider>.Instance);
        await p1.InitializeAsync();
        var thumb = p1.Current!.Thumbprint;

        // A second provider over the same directory + protection loads the stored cert.
        var p2 = new HttpsCertificateProvider(
            scopeFactory, sys, protection, time, dir, NullLogger<HttpsCertificateProvider>.Instance);
        await p2.InitializeAsync();

        Assert.Equal(thumb, p2.Current!.Thumbprint);
    }

    // ─────────────────────────── Helpers ───────────────────────────

    private (HttpsCertificateProvider provider, FakeConfig cfg, string dir, TestTime time) NewProvider(
        DateTimeOffset now)
    {
        var cfg = new FakeConfig();
        var dir = NewTempDir();
        var time = new TestTime(now);
        var provider = new HttpsCertificateProvider(
            NewScopeFactory(cfg), NewSysInfo(), new PassthroughProtection(), time, dir,
            NullLogger<HttpsCertificateProvider>.Instance);
        return (provider, cfg, dir, time);
    }

    private static ISystemInfoService NewSysInfo()
    {
        var sys = new Mock<ISystemInfoService>();
        sys.Setup(s => s.HostName).Returns("testhost");
        sys.Setup(s => s.GetNetworkAddresses())
            .Returns([new NetworkAddressInfo("eth0", "10.1.2.3", "IPv4")]);
        return sys.Object;
    }

    private static IServiceScopeFactory NewScopeFactory(IConfigRepository config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static string ReadSan(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
            if (ext.Oid?.Value == "2.5.29.17")
                return ext.Format(false);
        return "";
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kaimo-cert-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    // ── Test doubles ──

    private sealed class TestTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Identity "protection" — the provider's encryption is exercised elsewhere.</summary>
    private sealed class PassthroughProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    /// <summary>In-memory config store holding live object references (no JSON round-trip).</summary>
    private sealed class FakeConfig : IConfigRepository
    {
        private readonly Dictionary<string, object?> _store = new();

        public T Get<T>() => (T)_store[typeof(T) == typeof(HttpsCertificateSettings)
            ? HttpsCertificateSettings.ConfigKey
            : throw new NotSupportedException()]!;

        public Task<T> GetAsync<T>(string key, T fallback)
            => Task.FromResult(_store.TryGetValue(key, out var v) && v is T t ? t : fallback);

        public Task<T> GetFreshAsync<T>(string key, T fallback) => GetAsync(key, fallback);

        public Task SetAsync<T>(string key, T value)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task<double> GetDoubleAsync(string key, double fallback = 0d) => Task.FromResult(fallback);
        public Task SetManyAsync(Dictionary<string, object> values) => Task.CompletedTask;
        public Task<Dictionary<string, string>> GetSectionAsync(string prefix) => Task.FromResult(new Dictionary<string, string>());
        public Task<bool> DeleteAsync(string key) => Task.FromResult(_store.Remove(key));
    }
}
