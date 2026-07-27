using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services.Https;

/// <summary>
/// Owns the HTTPS server certificate Kestrel serves. Holds the current certificate
/// in memory (hot-swappable via <see cref="Current"/>, which Kestrel reads per
/// connection through a <c>ServerCertificateSelector</c>), generates a self-signed
/// one on first boot, renews it before expiry, and lets an admin upload their own.
///
/// The private key is persisted encrypted at rest: the PFX bytes are wrapped with
/// ASP.NET DataProtection and written to <c>{storageRoot}/.certs</c> — mirroring the
/// at-rest protection used for SMB NT hashes (<c>AesGcmNtHashProtector</c>).
/// </summary>
public sealed class HttpsCertificateProvider : IHttpsCertificateProvider
{
    private const string ProtectorPurpose = "KaimoFiles.Https.Certificate";
    private const string CertFileName = "current.pfx.protected";
    // serverAuth EKU
    private static readonly Oid ServerAuthOid = new("1.3.6.1.5.5.7.3.1");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISystemInfoService _sysInfo;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<HttpsCertificateProvider> _logger;
    private readonly string _certDir;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile X509Certificate2? _current;

    public HttpsCertificateProvider(
        IServiceScopeFactory scopeFactory,
        ISystemInfoService sysInfo,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        string certPath,
        ILogger<HttpsCertificateProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _sysInfo = sysInfo;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
        _logger = logger;
        _certDir = Path.Combine(certPath, ".certs");
    }

    /// <summary>The certificate currently served, or null before initialization.</summary>
    public X509Certificate2? Current => _current;

    private string CertPath => Path.Combine(_certDir, CertFileName);

    /// <summary>
    /// Loads the persisted certificate (if any) and generates a fresh self-signed one
    /// when none exists or an auto-managed one is (nearly) expired. Call once at startup,
    /// before the first TLS connection.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await LoadSettingsAsync();
            var persisted = TryLoadPersisted();

            if (persisted is not null)
            {
                _current = persisted;
                _logger.LogInformation(
                    "HTTPS certificate loaded (Subject={Subject}, Expires={Expires:u}, Mode={Mode})",
                    persisted.Subject, persisted.NotAfter.ToUniversalTime(), settings.Mode);
            }

            var needsFresh = settings.Mode == CertificateMode.Auto
                             && (_current is null || NeedsRenewal(_current, settings));

            if (_current is null && settings.Mode == CertificateMode.Custom)
            {
                // Custom mode but nothing persisted (e.g. wiped .certs dir): fall back to
                // an auto certificate so the server still serves HTTPS.
                _logger.LogWarning(
                    "Certificate mode is Custom but no certificate is stored — generating a self-signed fallback.");
                settings.Mode = CertificateMode.Auto;
                await SaveSettingsAsync(settings);
                needsFresh = true;
            }

            if (needsFresh)
                await GenerateAndStoreAsync(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renews the self-signed certificate if it is within the renewal window. No-op in
    /// Custom mode (an uploaded certificate is never auto-renewed). Returns true if a new
    /// certificate was issued and swapped in.
    /// </summary>
    public async Task<bool> EnsureValidAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await LoadSettingsAsync();
            if (settings.Mode != CertificateMode.Auto)
                return false;

            if (_current is not null && !NeedsRenewal(_current, settings))
                return false;

            await GenerateAndStoreAsync(settings);
            _logger.LogInformation(
                "HTTPS certificate renewed (new expiry {Expires:u})", _current!.NotAfter.ToUniversalTime());
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces a brand-new self-signed certificate (sets Mode=Auto).</summary>
    public async Task RegenerateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await LoadSettingsAsync();
            settings.Mode = CertificateMode.Auto;
            settings.Normalize();
            await SaveSettingsAsync(settings);
            await GenerateAndStoreAsync(settings);
            _logger.LogInformation("HTTPS certificate regenerated on request.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the served certificate with an administrator-supplied certificate (sets
    /// Mode=Custom, so it is never auto-renewed). The container format is auto-detected:
    /// PKCS#12 (.pfx/.p12) as well as PEM bundles (.pem/.crt/.cer) that carry a private
    /// key are accepted. Throws if the input cannot be read with the given password or
    /// carries no private key.
    /// </summary>
    public async Task ImportCustomAsync(
        byte[] certBytes, string? password, byte[]? keyBytes = null, CancellationToken ct = default)
    {
        // Parse + validate outside the lock — the loader throws on bad password / unknown input.
        var imported = keyBytes is { Length: > 0 }
            ? LoadFromSeparateFiles(certBytes, keyBytes, password) // cert + key uploaded separately
            : LoadWithPrivateKey(certBytes, password);             // single PFX / combined PEM

        if (!imported.HasPrivateKey)
        {
            imported.Dispose();
            throw new InvalidOperationException(
                "Das Zertifikat enthält keinen privaten Schlüssel und kann nicht für HTTPS verwendet werden. " +
                "Bitte eine .pfx/.p12- oder PEM-Datei hochladen, die auch den privaten Schlüssel enthält.");
        }

        // Normalize to a password-less PFX for uniform storage.
        var normalized = imported.Export(X509ContentType.Pfx);
        imported.Dispose();

        await _gate.WaitAsync(ct);
        try
        {
            var settings = await LoadSettingsAsync();
            settings.Mode = CertificateMode.Custom;
            await SaveSettingsAsync(settings);

            Persist(normalized);
            SwapCurrent(LoadPfx(normalized));
            _logger.LogInformation(
                "Custom HTTPS certificate imported (Subject={Subject}, Expires={Expires:u})",
                _current!.Subject, _current.NotAfter.ToUniversalTime());
        }
        finally
        {
            _gate.Release();
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    /// <summary>The public certificate in DER encoding (no private key) — for download.</summary>
    public byte[]? GetPublicCertDer() => _current?.Export(X509ContentType.Cert);

    /// <summary>The public certificate in PEM encoding (no private key) — for download.</summary>
    public string? GetPublicCertPem() => _current?.ExportCertificatePem();

    // ── internals ──

    private bool NeedsRenewal(X509Certificate2 cert, HttpsCertificateSettings settings)
    {
        var now = _time.GetUtcNow();
        var expires = cert.NotAfter.ToUniversalTime();
        return expires - now <= TimeSpan.FromDays(settings.RenewalLeadDays);
    }

    private async Task GenerateAndStoreAsync(HttpsCertificateSettings settings)
    {
        var (cert, pfx) = GenerateSelfSigned(settings);
        try
        {
            Persist(pfx);
            SwapCurrent(cert);
            _logger.LogInformation(
                "Self-signed HTTPS certificate issued (Subject={Subject}, Expires={Expires:u}, Lifetime={Days}d)",
                cert.Subject, cert.NotAfter.ToUniversalTime(), settings.LifetimeDays);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Builds a self-signed serverAuth certificate valid for <c>LifetimeDays</c>, with
    /// SANs for the host name, <c>localhost</c>, all local IP addresses and any extra
    /// entries configured by the admin. Returns the loaded certificate and its PFX bytes.
    /// </summary>
    public (X509Certificate2 Cert, byte[] Pfx) GenerateSelfSigned(HttpsCertificateSettings settings)
    {
        settings.Normalize();

        var host = string.IsNullOrWhiteSpace(settings.CommonName)
            ? (_sysInfo.HostName is { Length: > 0 } h ? h : "kaimo-fileserver")
            : settings.CommonName!;

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { ServerAuthOid }, critical: false));
        request.CertificateExtensions.Add(BuildSans(host, settings.AdditionalSans));

        var now = _time.GetUtcNow();
        var cert = request.CreateSelfSigned(
            now.AddMinutes(-5), now.AddDays(settings.LifetimeDays));

        // Export + reload so the key is portable/persistable across platforms.
        var pfx = cert.Export(X509ContentType.Pfx);
        cert.Dispose();
        return (LoadPfx(pfx), pfx);
    }

    private X509Extension BuildSans(string host, IEnumerable<string> extra)
    {
        var san = new SubjectAlternativeNameBuilder();
        var seenDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddEntry(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (IPAddress.TryParse(value, out var ip))
            {
                if (seenIp.Add(ip.ToString())) san.AddIpAddress(ip);
            }
            else if (seenDns.Add(value))
            {
                san.AddDnsName(value);
            }
        }

        AddEntry(host);
        AddEntry("localhost");
        AddEntry("127.0.0.1");
        AddEntry("::1");

        foreach (var addr in _sysInfo.GetNetworkAddresses())
            AddEntry(addr.Address);

        foreach (var e in extra)
            AddEntry(e);

        return san.Build();
    }

    private static X509Certificate2 LoadPfx(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);

    // ── Custom-certificate import: format auto-detection ──

    /// <summary>
    /// Loads a certificate together with its private key from an administrator upload,
    /// auto-detecting the container format. Supports PKCS#12 (.pfx/.p12) and PEM bundles
    /// (.pem/.crt/.cer) that concatenate a certificate and a — optionally
    /// password-encrypted — private key. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing German message when the input cannot be interpreted or carries
    /// no usable private key.
    /// </summary>
    private static X509Certificate2 LoadWithPrivateKey(byte[] data, string? password)
    {
        if (data is null || data.Length == 0)
            throw new InvalidOperationException("Die hochgeladene Datei ist leer.");

        // PEM is ASCII text; PKCS#12 is DER (binary). Sniff which one we got.
        var text = TryDecodeText(data);
        if (text is not null && text.Contains("-----BEGIN", StringComparison.Ordinal))
            return LoadFromPem(text, password);

        return LoadFromPkcs12(data, password);
    }

    private static X509Certificate2 LoadFromPkcs12(byte[] data, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(data, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            // A bare DER certificate (.crt/.cer without key) parses here too — give a
            // targeted hint instead of a generic "unreadable" message.
            if (IsPublicOnlyCertificate(data))
                throw new InvalidOperationException(
                    "Die Datei enthält nur ein öffentliches Zertifikat ohne privaten Schlüssel. " +
                    "Für HTTPS wird eine .pfx/.p12- oder PEM-Datei benötigt, die auch den privaten Schlüssel enthält.", ex);

            throw new InvalidOperationException(
                "Die Datei konnte nicht als Zertifikat gelesen werden (falsches Passwort oder unbekanntes Format). " +
                "Unterstützt werden .pfx/.p12 sowie PEM (.pem/.crt/.cer).", ex);
        }
    }

    private static X509Certificate2 LoadFromPem(string pem, string? password)
    {
        if (!pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "In der Datei wurde kein Zertifikat (-----BEGIN CERTIFICATE-----) gefunden.");

        var hasEncryptedKey = pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        var hasPlainKey =
            pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
            pem.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal) ||
            pem.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal);

        if (!hasEncryptedKey && !hasPlainKey)
            throw new InvalidOperationException(
                "Die PEM-Datei enthält nur ein öffentliches Zertifikat ohne privaten Schlüssel. " +
                "Bitte den privaten Schlüssel (z. B. aus privkey.pem) an dieselbe Datei anhängen oder eine .pfx/.p12 hochladen.");

        try
        {
            // CreateFrom*Pem scans the text for the first CERTIFICATE and the first key
            // block and ignores every other PEM object (e.g. chain intermediates), so
            // passing the whole bundle as both arguments works for concatenated files.
            // The key from CreateFromPem is ephemeral and unusable by Windows SChannel
            // until it is round-tripped through a PKCS#12 blob — the caller does exactly
            // that (Export → LoadPfx).
            return hasPlainKey
                ? X509Certificate2.CreateFromPem(pem, pem)
                : X509Certificate2.CreateFromEncryptedPem(pem, pem, password ?? string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                hasPlainKey
                    ? "Zertifikat und privater Schlüssel aus der PEM-Datei konnten nicht gelesen werden (passen Zertifikat und Schlüssel zusammen?)."
                    : "Der verschlüsselte private Schlüssel konnte nicht gelesen werden — bitte das Passwort prüfen.", ex);
        }
    }

    /// <summary>
    /// Combines a certificate file and a separate private-key file (as uploaded in two
    /// fields) into one certificate. Each part may be PEM or DER: the certificate is
    /// normalized to a PEM block, the key to a matching PEM key block, and the pair is then
    /// loaded through the same PEM path as a combined bundle.
    /// </summary>
    private static X509Certificate2 LoadFromSeparateFiles(byte[] certBytes, byte[] keyBytes, string? password)
    {
        var certPem = CertToPem(certBytes);
        var keyPem = KeyToPem(keyBytes, password);
        return LoadFromPem(certPem + "\n" + keyPem, password);
    }

    /// <summary>Returns the certificate as PEM text, accepting either PEM or DER input.</summary>
    private static string CertToPem(byte[] certBytes)
    {
        var text = TryDecodeText(certBytes);
        if (text is not null && text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            return text;

        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(certBytes);
            return cert.ExportCertificatePem();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Die Zertifikatsdatei konnte nicht gelesen werden (weder PEM noch DER).", ex);
        }
    }

    /// <summary>
    /// Returns the private key as PEM text, accepting a PEM key (plain or encrypted) or a
    /// DER-encoded key (PKCS#8 plain/encrypted, PKCS#1 RSA or SEC1 EC).
    /// </summary>
    private static string KeyToPem(byte[] keyBytes, string? password)
    {
        var text = TryDecodeText(keyBytes);
        if (text is not null
            && text.Contains("-----BEGIN", StringComparison.Ordinal)
            && text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return text; // already a PEM key (plain or encrypted)
        }

        // Binary DER: wrap the bytes with the PEM label that matches their encoding.
        var label = DetectDerKeyLabel(keyBytes, password);
        if (label is null)
            throw new InvalidOperationException(
                "Die Schlüsseldatei konnte nicht als privater Schlüssel gelesen werden. " +
                "Bitte den Schlüssel als PEM (z. B. privkey.pem) oder eine .pfx/.p12 hochladen.");

        return new string(PemEncoding.Write(label, keyBytes));
    }

    /// <summary>Probes a DER key blob to find the PEM label that describes its encoding.</summary>
    private static string? DetectDerKeyLabel(byte[] der, string? password)
    {
        if (TryImportRsa(rsa => rsa.ImportPkcs8PrivateKey(der, out _))
            || TryImportEc(ec => ec.ImportPkcs8PrivateKey(der, out _)))
            return "PRIVATE KEY";

        if (!string.IsNullOrEmpty(password)
            && (TryImportRsa(rsa => rsa.ImportEncryptedPkcs8PrivateKey(password, der, out _))
                || TryImportEc(ec => ec.ImportEncryptedPkcs8PrivateKey(password, der, out _))))
            return "ENCRYPTED PRIVATE KEY";

        if (TryImportRsa(rsa => rsa.ImportRSAPrivateKey(der, out _)))
            return "RSA PRIVATE KEY";

        if (TryImportEc(ec => ec.ImportECPrivateKey(der, out _)))
            return "EC PRIVATE KEY";

        return null;
    }

    private static bool TryImportRsa(Action<RSA> import)
    {
        using var rsa = RSA.Create();
        try { import(rsa); return true; }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException
                                       or System.Formats.Asn1.AsnContentException)
        {
            return false;
        }
    }

    private static bool TryImportEc(Action<ECDsa> import)
    {
        using var ec = ECDsa.Create();
        try { import(ec); return true; }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException
                                       or System.Formats.Asn1.AsnContentException)
        {
            return false;
        }
    }

    private static bool IsPublicOnlyCertificate(byte[] data)
    {
        try
        {
            using var _ = X509CertificateLoader.LoadCertificate(data);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Decodes the bytes as UTF-8 text, or returns null when they are binary (contain NUL).</summary>
    private static string? TryDecodeText(byte[] data)
    {
        foreach (var b in data)
            if (b == 0) return null; // DER/PKCS#12 contains NUL bytes; PEM never does.
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private void SwapCurrent(X509Certificate2 next) => _current = next;

    private void Persist(byte[] pfx)
    {
        Directory.CreateDirectory(_certDir);
        var protectedBytes = _protector.Protect(pfx);
        // Write atomically: temp file + move, so a crash mid-write can't corrupt the store.
        var tmp = CertPath + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, CertPath, overwrite: true);
    }

    private X509Certificate2? TryLoadPersisted()
    {
        try
        {
            if (!File.Exists(CertPath)) return null;
            var raw = File.ReadAllBytes(CertPath);
            var pfx = _protector.Unprotect(raw);
            try
            {
                return LoadPfx(pfx);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored HTTPS certificate could not be read — a new one will be generated.");
            return null;
        }
    }

    private async Task<HttpsCertificateSettings> LoadSettingsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var settings = await config.GetAsync(HttpsCertificateSettings.ConfigKey, HttpsCertificateSettings.Default());
        settings.Normalize();
        return settings;
    }

    private async Task SaveSettingsAsync(HttpsCertificateSettings settings)
    {
        settings.Normalize();
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await config.SetAsync(HttpsCertificateSettings.ConfigKey, settings);
    }
}
