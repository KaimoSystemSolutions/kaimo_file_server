using System.Security.Cryptography.X509Certificates;

namespace Kaimo_File_Server.Web.Services.Https;

/// <summary>
/// Owns the HTTPS server certificate Kestrel serves. Implementations hold the current
/// certificate in memory (hot-swappable), generate a self-signed one on first boot,
/// renew it before expiry and allow an administrator to upload their own.
/// </summary>
public interface IHttpsCertificateProvider
{
    /// <summary>The certificate currently served, or null before initialization.</summary>
    X509Certificate2? Current { get; }

    /// <summary>Loads or generates the certificate. Call once at startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Renews the auto-managed certificate if it is within the renewal window.</summary>
    Task<bool> EnsureValidAsync(CancellationToken ct = default);

    /// <summary>Forces a brand-new self-signed certificate (sets auto mode).</summary>
    Task RegenerateAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the served certificate with an admin-supplied certificate (custom mode).
    /// The format is auto-detected: PKCS#12 (.pfx/.p12) and PEM bundles (.pem/.crt/.cer)
    /// carrying a private key are accepted. When the certificate and the private key come
    /// as separate files, pass the key via <paramref name="keyBytes"/>.
    /// </summary>
    Task ImportCustomAsync(
        byte[] certBytes, string? password, byte[]? keyBytes = null, CancellationToken ct = default);

    /// <summary>The public certificate in DER encoding (no private key), for download.</summary>
    byte[]? GetPublicCertDer();

    /// <summary>The public certificate in PEM encoding (no private key), for download.</summary>
    string? GetPublicCertPem();
}
