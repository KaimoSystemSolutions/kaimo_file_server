namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// A versioned path whose file the retention sweep found missing, and since when. The sweep
/// removes the history once the file has stayed missing for the orphan grace period; this
/// row keeps that clock running across restarts. Rebuilt on every sweep of the share.
/// </summary>
public sealed class FileVersionMissingPath
{
    public Guid ShareId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime MissingSinceUtc { get; set; }
}
