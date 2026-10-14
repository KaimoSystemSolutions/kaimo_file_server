namespace Kaimo_File_Server.Core.Domain;

/// <summary>How a share's recycle-bin size limit is determined.</summary>
public enum RecycleLimitMode
{
    /// <summary>Use the global default (<c>recycle.maxSizeBytes</c>).</summary>
    Inherit = 0,

    /// <summary>Use <see cref="ShareDefinition.RecycleMaxSizeBytes"/>.</summary>
    Custom = 1,

    /// <summary>No size limit, whatever the global default says.</summary>
    Unlimited = 2,
}

/// <summary>The editable recycle-bin limits of a share (see <see cref="ShareDefinition"/>).</summary>
public sealed record RecycleLimits(RecycleLimitMode Mode, long? MaxSizeBytes, int? RetentionDays)
{
    public bool IsValid => ShareDefinition.IsValidRecycleLimits(Mode, MaxSizeBytes, RetentionDays);
}

/// <summary>
/// Tracking row of the recycle-bin janitor: one per file or folder inside a recycle bin.
/// <see cref="FirstSeenUtc"/> is the effective deletion time (the first janitor run that saw
/// the item with this <see cref="CtimeTicks"/>). Pure bookkeeping: the table can be dropped
/// at any time and is rebuilt from disk, at worst delaying purges by one retention period.
/// </summary>
public sealed class RecycleBinEntry
{
    public Guid ShareId { get; set; }

    /// <summary>Share-relative path, e.g. <c>.RECYCLE_BIN/a/b.txt</c> or <c>&lt;uid&gt;/.RECYCLE_BIN/x</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Change-time fingerprint; a different value means the item was replaced.</summary>
    public long CtimeTicks { get; set; }

    /// <summary>File size in bytes; 0 for directories.</summary>
    public long SizeBytes { get; set; }

    public DateTime FirstSeenUtc { get; set; }
}
