namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// An anonymous, tokenised public download link for a single file or folder inside a
/// network share. The link is opened by an unauthenticated visitor; all file access runs
/// under the creating user's identity (<see cref="CreatedByUserId"/>) and is re-checked
/// against ACLs at access time. Policy fields (window, access count, rate cap, password)
/// are enforced both when the visitor unlocks the link and again when bytes are streamed.
/// </summary>
public sealed class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>URL-safe, cryptographically random, unique public token (the "/shared/{token}" segment).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Target network share.</summary>
    public Guid ShareId { get; set; }

    /// <summary>
    /// Share-relative path of the shared file or folder. For a folder link this is also the
    /// navigation root: the public browser is confined to this path and its descendants.
    /// </summary>
    public string RootRelativePath { get; set; } = string.Empty;

    /// <summary>True when <see cref="RootRelativePath"/> points at a folder (browsable + ZIP download).</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Display name of the shared item (file/folder name), used in the UI and as the download filename.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The user whose identity anonymous downloads and folder listings run under.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>BCrypt hash of the optional link password; null when the link is not password-protected.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Chosen base address the link URL is built from (one of the admin-configured allowlist
    /// entries). Null falls back to the configured default at display time.
    /// </summary>
    public string? BaseAddress { get; set; }

    /// <summary>Link is inactive before this instant; null = active immediately.</summary>
    public DateTime? StartsAtUtc { get; set; }

    /// <summary>Link is inactive after this instant; null = never expires.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Maximum number of download operations; null = unlimited.</summary>
    public int? MaxAccessCount { get; set; }

    /// <summary>Number of download operations performed so far (a page view does not count).</summary>
    public int AccessCount { get; set; }

    /// <summary>Download bandwidth cap in bytes per second; null = unlimited.</summary>
    public long? MaxBytesPerSecond { get; set; }

    /// <summary>Admin/creator on-off switch, independent of the time window.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the link is enabled and within its start/end window at <paramref name="nowUtc"/>.
    /// Gates browsing a folder link (which does not consume an access). Ignores the access count
    /// and the password (checked separately).
    /// </summary>
    public bool IsWindowOpen(DateTime nowUtc)
        => IsEnabled
           && (StartsAtUtc is null || nowUtc >= StartsAtUtc)
           && (ExpiresAtUtc is null || nowUtc <= ExpiresAtUtc);

    /// <summary>
    /// Whether the link currently permits a download at <paramref name="nowUtc"/>: within the
    /// window and not exhausted. The authoritative per-download check is the atomic
    /// <c>TryConsumeAccessAsync</c>; this mirrors it for display purposes.
    /// </summary>
    public bool IsCurrentlyActive(DateTime nowUtc)
        => IsWindowOpen(nowUtc)
           && (MaxAccessCount is null || AccessCount < MaxAccessCount);
}
