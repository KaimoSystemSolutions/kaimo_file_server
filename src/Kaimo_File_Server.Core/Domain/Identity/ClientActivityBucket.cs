namespace Kaimo_File_Server.Core.Domain.Identity;

/// <summary>
/// REST API and WebDAV requests of one client address within one hour. Hourly buckets keep
/// the table small enough for long-term analysis while still showing when traffic peaked.
/// </summary>
public sealed class ClientActivityBucket
{
    public long Id { get; set; }

    public string Address { get; set; } = string.Empty;

    /// <summary>Start of the hour (UTC) the counters belong to.</summary>
    public DateTime HourUtc { get; set; }

    public long ApiRequests { get; set; }

    public long WebDavRequests { get; set; }

    /// <summary>Requests answered with 401, 403 or 429.</summary>
    public long RejectedRequests { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>Path of the last request in this hour (without query string).</summary>
    public string LastPath { get; set; } = string.Empty;
}
