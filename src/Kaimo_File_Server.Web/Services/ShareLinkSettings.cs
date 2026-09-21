namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Admin-configured settings for public share links, persisted as one JSON blob via
/// <c>IConfigRepository</c> under <see cref="ConfigKey"/>. Holds the allowlist of base
/// addresses (e.g. the reverse-proxy URLs the server is reachable under) a link URL may be
/// built from, which one is the default, and whether a link creator may pick a non-default.
/// </summary>
public sealed class ShareLinkSettings
{
    public const string ConfigKey = "sharelinks.settings";
    public const int MaxAddresses = 5;

    /// <summary>Up to <see cref="MaxAddresses"/> absolute base URLs (no trailing slash).</summary>
    public List<string> BaseAddresses { get; set; } = new();

    /// <summary>Index into <see cref="BaseAddresses"/> used as the default when a link picks none.</summary>
    public int DefaultIndex { get; set; }

    /// <summary>Whether a link creator may choose a non-default address from the allowlist.</summary>
    public bool AllowUserChosenAddress { get; set; } = true;

    public void Normalize()
    {
        BaseAddresses = (BaseAddresses ?? new())
            .Select(a => (a ?? string.Empty).Trim().TrimEnd('/'))
            .Where(a => a.Length > 0 && Uri.TryCreate(a, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAddresses)
            .ToList();

        DefaultIndex = BaseAddresses.Count == 0
            ? 0
            : Math.Clamp(DefaultIndex, 0, BaseAddresses.Count - 1);
    }

    /// <summary>The configured default base address, or null when none is set.</summary>
    public string? DefaultAddress =>
        BaseAddresses.Count == 0 ? null : BaseAddresses[Math.Clamp(DefaultIndex, 0, BaseAddresses.Count - 1)];

    /// <summary>
    /// The base address a link should use: its own choice when still allowed and valid,
    /// otherwise the configured default. Null when no addresses are configured (the link then
    /// resolves as a host-relative URL).
    /// </summary>
    public string? ResolveBaseFor(string? linkChoice)
    {
        if (!string.IsNullOrWhiteSpace(linkChoice)
            && AllowUserChosenAddress
            && BaseAddresses.Contains(linkChoice, StringComparer.OrdinalIgnoreCase))
            return linkChoice.TrimEnd('/');

        return DefaultAddress;
    }

    public static ShareLinkSettings Default() => new();
}
