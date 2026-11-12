using System.Net;

namespace Kaimo_File_Server.Core.Security.Authentication;

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
