namespace Kaimo_File_Server.Web.Services.Https;

/// <summary>
/// How the HTTPS server certificate is sourced.
/// </summary>
public enum CertificateMode
{
    /// <summary>A self-signed certificate the server generates and auto-renews.</summary>
    Auto,

    /// <summary>A certificate the administrator uploaded — never auto-renewed.</summary>
    Custom,
}

/// <summary>
/// Runtime-configurable options for the HTTPS server certificate, edited on the
/// settings page ("Zertifikat" tab) and persisted as a JSON object under
/// <see cref="ConfigKey"/> via <c>IConfigRepository</c>.
///
/// The defaults produce a self-signed certificate with a 90-day lifetime that is
/// renewed automatically ~3 weeks before it expires, so an unconfigured system
/// serves HTTPS out of the box without any manual certificate handling.
/// </summary>
public sealed class HttpsCertificateSettings
{
    public const string ConfigKey = "https.certificate";

    /// <summary>The two supported lifetimes (in days): one month or three months.</summary>
    public const int LifetimeOneMonth = 30;
    public const int LifetimeThreeMonths = 90;

    /// <summary>Where the current certificate comes from. See <see cref="CertificateMode"/>.</summary>
    public CertificateMode Mode { get; set; } = CertificateMode.Auto;

    /// <summary>Validity of a freshly generated self-signed certificate, in days (30 or 90).</summary>
    public int LifetimeDays { get; set; } = LifetimeThreeMonths;

    /// <summary>
    /// Renew a self-signed certificate once its remaining validity drops below this
    /// many days. Clamped to a sane fraction of <see cref="LifetimeDays"/> so it can
    /// never exceed the lifetime itself.
    /// </summary>
    public int RenewalLeadDays { get; set; } = 21;

    /// <summary>
    /// Subject common name (CN) of the generated certificate. When null/blank the
    /// host name is used. Also added as a DNS SAN entry.
    /// </summary>
    public string? CommonName { get; set; }

    /// <summary>
    /// Extra host names / IP addresses to include as Subject Alternative Names, on
    /// top of the automatically added host name, <c>localhost</c> and local IPs.
    /// </summary>
    public List<string> AdditionalSans { get; set; } = new();

    public static HttpsCertificateSettings Default() => new();

    /// <summary>
    /// Repairs nonsensical values in place: the lifetime is snapped to the nearest
    /// supported value (30 or 90 days) and the renewal lead is clamped so it stays
    /// between 1 day and half the lifetime.
    /// </summary>
    public void Normalize()
    {
        LifetimeDays = LifetimeDays <= (LifetimeOneMonth + LifetimeThreeMonths) / 2
            ? LifetimeOneMonth
            : LifetimeThreeMonths;

        var maxLead = Math.Max(1, LifetimeDays / 2);
        RenewalLeadDays = Math.Clamp(RenewalLeadDays, 1, maxLead);

        AdditionalSans = AdditionalSans
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CommonName = string.IsNullOrWhiteSpace(CommonName) ? null : CommonName.Trim();
    }
}
