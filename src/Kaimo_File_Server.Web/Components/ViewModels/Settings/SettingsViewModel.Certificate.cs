using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Services.Https;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>HTTPS server-certificate display, settings and (re)issuance/import.</summary>
public partial class SettingsViewModel
{
    // ── HTTPS Certificate ──

    /// <summary>Certificate settings (mode, lifetime, CN, extra SANs) — working copy.</summary>
    public HttpsCertificateSettings CertSettings { get; private set; } = HttpsCertificateSettings.Default();

    /// <summary>Subject (CN) of the currently served certificate, or "—" when none.</summary>
    public string CertSubject { get; private set; } = "—";

    /// <summary>Issuer of the current certificate ("self" when self-signed).</summary>
    public string CertIssuer { get; private set; } = "—";

    /// <summary>Subject Alternative Names of the current certificate.</summary>
    public IReadOnlyList<string> CertSans { get; private set; } = [];

    public DateTime? CertIssuedUtc { get; private set; }
    public DateTime? CertExpiresUtc { get; private set; }
    public string CertThumbprint { get; private set; } = "";

    /// <summary>True if the current certificate is self-signed (subject == issuer).</summary>
    public bool CertIsSelfSigned { get; private set; }

    /// <summary>True while there is a certificate loaded to display.</summary>
    public bool HasCertificate { get; private set; }

    /// <summary>Days remaining until the current certificate expires (may be negative).</summary>
    public int? CertDaysRemaining =>
        CertExpiresUtc is { } exp ? (int)Math.Floor((exp - DateTime.UtcNow).TotalDays) : null;

    /// <summary>Working copy of the extra-SAN list as a single comma/newline separated string.</summary>
    public string CertAdditionalSansText { get; set; } = "";

    /// <summary>Working copy of the custom-certificate password entered in the upload form.</summary>
    public string CustomCertPassword { get; set; } = "";

    /// <summary>Reads the current certificate + settings into the view state.</summary>
    public async Task LoadCertificateStateAsync()
    {
        if (!CanManageCertificates) return;

        CertSettings = await _config.GetAsync(
            HttpsCertificateSettings.ConfigKey, HttpsCertificateSettings.Default());
        CertSettings.Normalize();
        CertAdditionalSansText = string.Join(", ", CertSettings.AdditionalSans);

        var cert = _certProvider.Current;
        HasCertificate = cert is not null;
        if (cert is null)
        {
            CertSubject = CertIssuer = "—";
            CertSans = [];
            CertIssuedUtc = CertExpiresUtc = null;
            CertThumbprint = "";
            CertIsSelfSigned = false;
            return;
        }

        CertSubject = cert.Subject;
        CertIssuer = cert.Issuer;
        CertIsSelfSigned = string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal);
        CertIssuedUtc = cert.NotBefore.ToUniversalTime();
        CertExpiresUtc = cert.NotAfter.ToUniversalTime();
        CertThumbprint = cert.Thumbprint;
        CertSans = ReadSans(cert);
    }

    /// <summary>Extracts the DNS/IP Subject Alternative Names for display.</summary>
    private static IReadOnlyList<string> ReadSans(
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // subjectAltName
            // FormatValue gives a readable "DNS Name=…, IP Address=…" list.
            return ext.Format(false)
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        return [];
    }

    /// <summary>Persists lifetime/CN/SAN settings and reissues a self-signed cert with them.</summary>
    public async Task<bool> SaveCertificateSettingsAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        CertSettings.AdditionalSans = CertAdditionalSansText
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        CertSettings.Normalize();

        try
        {
            await _config.SetAsync(HttpsCertificateSettings.ConfigKey, CertSettings);
            // Reissue with the new lifetime/CN/SANs so the change is visible immediately.
            await _certProvider.RegenerateAsync();
            await LoadCertificateStateAsync();

            _logger.LogInformation(
                "HTTPS certificate settings saved (LifetimeDays={Days}) and certificate reissued",
                CertSettings.LifetimeDays);
            SuccessMessage = "Zertifikatseinstellungen gespeichert und neues Zertifikat erzeugt.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save certificate settings");
            ErrorMessage = "Zertifikatseinstellungen konnten nicht gespeichert werden.";
            return false;
        }
    }

    /// <summary>Forces a fresh self-signed certificate right now.</summary>
    public async Task<bool> RegenerateCertificateNowAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        try
        {
            await _certProvider.RegenerateAsync();
            await LoadCertificateStateAsync();
            SuccessMessage = "Neues selbstsigniertes Zertifikat erzeugt. Es wird ohne Neustart ausgeliefert.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate certificate");
            ErrorMessage = "Zertifikat konnte nicht neu erzeugt werden.";
            return false;
        }
    }

    /// <summary>
    /// Replaces the served certificate with an admin-supplied certificate. Accepts
    /// PKCS#12 (.pfx/.p12) as well as PEM bundles (.pem/.crt/.cer) with a private key; the
    /// format is auto-detected in the provider. When the private key is a separate file,
    /// pass it via <paramref name="keyBytes"/>.
    /// </summary>
    public async Task<bool> ImportCustomCertificateAsync(byte[] certBytes, byte[]? keyBytes = null)
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!CanManageCertificates)
        {
            ErrorMessage = Resources.Web_Settings_NoPermissionChange;
            return false;
        }

        if (certBytes.Length == 0)
        {
            ErrorMessage = "Bitte zuerst eine Zertifikatsdatei (.pfx/.p12 oder .pem/.crt/.cer) auswählen.";
            return false;
        }

        try
        {
            await _certProvider.ImportCustomAsync(
                certBytes,
                string.IsNullOrEmpty(CustomCertPassword) ? null : CustomCertPassword,
                keyBytes);
            CustomCertPassword = "";
            await LoadCertificateStateAsync();
            SuccessMessage = "Eigenes Zertifikat übernommen. Die automatische Erneuerung ist dafür pausiert.";
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import custom certificate");
            ErrorMessage = "Eigenes Zertifikat konnte nicht übernommen werden.";
            return false;
        }
    }

    /// <summary>The public certificate as a base64 DER string, for a client-side download link.</summary>
    public string? GetPublicCertDerBase64()
    {
        var der = _certProvider.GetPublicCertDer();
        return der is null ? null : Convert.ToBase64String(der);
    }

    /// <summary>The public certificate as a base64 PEM string, for a client-side download link.</summary>
    public string? GetPublicCertPemBase64()
    {
        var pem = _certProvider.GetPublicCertPem();
        return pem is null ? null : Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem));
    }
}
