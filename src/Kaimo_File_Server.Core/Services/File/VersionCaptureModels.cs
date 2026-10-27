using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>Why a snapshot request did or did not produce a version.</summary>
public enum VersionCaptureOutcome
{
    /// <summary>A new version was stored.</summary>
    Created,
    /// <summary>The newest version already holds this content.</summary>
    Unchanged,
    /// <summary>Versioning is switched off globally.</summary>
    Disabled,
    /// <summary>The path matches an exclusion pattern.</summary>
    Excluded,
    /// <summary>The content exceeds the maximum versioned file size.</summary>
    TooLarge,
    /// <summary>The version storage is below its free-space reserve.</summary>
    LowSpace,
    /// <summary>The share lies outside every storage pool.</summary>
    NoPool,
}

/// <summary>Result of <see cref="IFileVersionService.CaptureVersionAsync"/>.</summary>
public sealed record VersionCaptureResult(VersionCaptureOutcome Outcome, FileVersion? Version = null)
{
    /// <summary>
    /// Whether the captured content is recoverable from the version history afterwards,
    /// i.e. it is safe to overwrite the live file.
    /// </summary>
    public bool PreservesContent => Outcome is VersionCaptureOutcome.Created or VersionCaptureOutcome.Unchanged;
}

/// <summary>
/// Thrown when an operation that would overwrite a file cannot first preserve its current
/// content as a version (e.g. restoring an older version while the pool is low on space).
/// </summary>
public sealed class VersionBackupUnavailableException(VersionCaptureOutcome outcome)
    : InvalidOperationException($"The current content could not be preserved as a version ({outcome}).")
{
    public VersionCaptureOutcome Outcome { get; } = outcome;
}
