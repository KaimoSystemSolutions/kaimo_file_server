using System.Net;

namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Admin-defined rules that limit where users, groups or departments may sign in from,
/// persisted as one JSON blob in <c>ConfigSettings</c> under <see cref="ConfigKey"/>.
/// A user covered by no enabled rule is unrestricted. A covered user is allowed when at least
/// one covering rule matches the request: its host (the address the server was reached under)
/// or its client IP. Enforced for the web UI, the client API and WebDAV. SMB has no per-user
/// rules; <see cref="SmbAllowedNetworks"/> limits which client networks may reach Samba at all.
/// </summary>
public sealed class AccessRestrictionSettings
{
    public const string ConfigKey = "security.access.restrictions";

    /// <summary>
    /// Set to <c>true</c> to ignore every rule and the SMB network list (recovery after a lockout).
    /// For SMB it must be set on the <c>smb-bridge</c> container.
    /// </summary>
    public const string DisableEnvironmentVariable = "KAIMO_ACCESS_RESTRICTIONS_DISABLED";

    public List<AccessRule> Rules { get; set; } = new();

    /// <summary>
    /// Client networks (CIDR or single address) Samba accepts connections from, for every user
    /// (<c>hosts allow</c>). Empty allows every network. The Samba container always allows
    /// loopback as well, for its health checks.
    /// </summary>
    public List<string> SmbAllowedNetworks { get; set; } = new();

    /// <summary>Most entries of <see cref="SmbAllowedNetworks"/>; the Samba config sync rejects longer lists.</summary>
    public const int MaxSmbAllowedNetworks = 256;

    public bool HasEnabledRules => Rules.Any(r => r.Enabled);

    public static AccessRestrictionSettings Default() => new();

    /// <summary>
    /// The valid entries of <see cref="SmbAllowedNetworks"/> in canonical CIDR notation, without
    /// duplicates and at most <see cref="MaxSmbAllowedNetworks"/>; invalid entries are dropped, so a
    /// stale value can never break the Samba config.
    /// </summary>
    public IReadOnlyList<string> SmbHostsAllow()
        => SmbAllowedNetworks
            .Select(entry => TryParseNetwork(entry, out var network) ? network.ToString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSmbAllowedNetworks)
            .ToList();

    /// <summary>Whether the restrictions are switched off via <see cref="DisableEnvironmentVariable"/>.</summary>
    public static bool DisabledByEnvironment
        => string.Equals(Environment.GetEnvironmentVariable(DisableEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates the rules for one user. <paramref name="departmentIds"/> must include the
    /// ancestors of the user's departments, so a rule on a parent department also covers
    /// members of its sub-departments.
    /// </summary>
    public bool IsAllowed(
        Guid userId,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid> departmentIds,
        string? remoteAddress,
        string? host)
    {
        var covering = Rules.Where(r => r.Enabled && r.Covers(userId, groupIds, departmentIds)).ToList();
        if (covering.Count == 0) return true;

        var address = ParseAddress(remoteAddress);
        var normalizedHost = NormalizeHost(host);
        return covering.Any(r => r.Matches(address, normalizedHost));
    }

    /// <summary>Parses a client address; IPv4-mapped IPv6 addresses are mapped back to IPv4.</summary>
    public static IPAddress? ParseAddress(string? value)
    {
        if (!IPAddress.TryParse(value?.Trim(), out var address)) return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>Lower-case host name without port; null when empty.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var value = host.Trim();
        if (Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri))
            value = uri.Host;
        return value.Trim('[', ']').ToLowerInvariant();
    }

    /// <summary>
    /// Parses an IP range in CIDR notation, or a single address (which becomes a /32 or /128
    /// network). IPv4-mapped IPv6 ranges become IPv4 ranges, like <see cref="ParseAddress"/> maps
    /// client addresses. False for anything else.
    /// </summary>
    public static bool TryParseNetwork(string? value, out IPNetwork network)
    {
        network = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains('/'))
        {
            if (!IPNetwork.TryParse(text, out network)) return false;
            // A mapped base address implies a prefix of at least 96 (the ::ffff: bits are fixed).
            if (network.BaseAddress.IsIPv4MappedToIPv6)
                network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
            return true;
        }
        if (ParseAddress(text) is not { } single) return false;
        network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }
}

/// <summary>One access restriction rule: who it covers and where they may come from.</summary>
public sealed class AccessRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public List<Guid> UserIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();

    /// <summary>Allowed host names (the address the server was reached under), lower-case.</summary>
    public List<string> Hosts { get; set; } = new();

    /// <summary>Allowed client IP ranges in CIDR notation.</summary>
    public List<string> Networks { get; set; } = new();

    /// <summary>A deep copy, so an editor never mutates the cached settings instance.</summary>
    public AccessRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        UserIds = UserIds.ToList(),
        GroupIds = GroupIds.ToList(),
        DepartmentIds = DepartmentIds.ToList(),
        Hosts = Hosts.ToList(),
        Networks = Networks.ToList(),
    };

    public bool Covers(Guid userId, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<Guid> departmentIds)
        => UserIds.Contains(userId)
           || GroupIds.Any(groupIds.Contains)
           || DepartmentIds.Any(departmentIds.Contains);

    /// <summary>Whether the request host is an allowed host or the client IP lies in an allowed range.</summary>
    public bool Matches(IPAddress? address, string? normalizedHost)
    {
        if (normalizedHost is not null && Hosts.Any(h => string.Equals(h, normalizedHost, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (address is null) return false;
        foreach (var entry in Networks)
            if (AccessRestrictionSettings.TryParseNetwork(entry, out var network) && network.Contains(address))
                return true;
        return false;
    }
}
