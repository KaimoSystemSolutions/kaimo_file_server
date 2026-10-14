using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Admin-configured settings for public share links, persisted as one JSON blob via
/// <c>IConfigRepository</c> under <see cref="ConfigKey"/>: the default-domain restriction,
/// the upload-link switch and size ceiling. The address a link URL is built from comes from <see cref="ServerDomainSettings"/>.
/// </summary>
public sealed class ShareLinkSettings
{
    public const string ConfigKey = "sharelinks.settings";

    /// <summary>Default and hard upper bound for a single file uploaded through an upload link (4 GiB).</summary>
    public const long DefaultMaxUploadFileSizeBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Legacy: read only to derive <see cref="ServerDomainSettings"/> before its first save.</summary>
    public List<string> BaseAddresses { get; set; } = new();

    /// <summary>Legacy: read only to derive <see cref="ServerDomainSettings"/> before its first save.</summary>
    public int DefaultIndex { get; set; }

    /// <summary>
    /// Whether anonymous upload links are enabled at all. Off by default: while off, no upload
    /// link can be created and existing upload links accept no files.
    /// </summary>
    public bool AllowUploadLinks { get; set; }

    /// <summary>
    /// Global ceiling for a single file uploaded through an upload link (a link may only lower
    /// it). Clamped to 1 byte … <see cref="DefaultMaxUploadFileSizeBytes"/>.
    /// </summary>
    public long MaxUploadFileSizeBytes { get; set; } = DefaultMaxUploadFileSizeBytes;

    /// <summary>
    /// Whether public links (download and upload) open only under the host of the default
    /// server domain. On by default; without a default domain, every host is allowed.
    /// </summary>
    public bool RestrictToDefaultDomain { get; set; } = true;

    /// <summary>Whether a public link may be served under the request host <paramref name="host"/>.</summary>
    public static async Task<bool> AllowsHostAsync(IConfigRepository config, string? host)
        => !(await config.GetAsync(ConfigKey, Default())).RestrictToDefaultDomain
           || (await ServerDomainSettings.LoadAsync(config)).AllowsPublicLinkHost(host);

    /// <summary>The effective per-file limit for <paramref name="link"/>: its own cap, bounded by the global one.</summary>
    public long EffectiveMaxFileSize(Kaimo_File_Server.Core.Domain.ShareLink link)
        => link.MaxFileSizeBytes is long own and > 0 ? Math.Min(own, MaxUploadFileSizeBytes) : MaxUploadFileSizeBytes;

    public void Normalize()
    {
        MaxUploadFileSizeBytes = MaxUploadFileSizeBytes <= 0
            ? DefaultMaxUploadFileSizeBytes
            : Math.Min(MaxUploadFileSizeBytes, DefaultMaxUploadFileSizeBytes);
    }

    public static ShareLinkSettings Default() => new();
}
