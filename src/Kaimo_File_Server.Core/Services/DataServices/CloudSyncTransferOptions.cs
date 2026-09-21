using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>Runtime helpers for applying one mapping's persisted transfer constraints.</summary>
public sealed class CloudSyncTransferOptions
{
    private readonly CloudSyncAdvancedSettings _settings;

    public CloudSyncTransferOptions(
        CloudSyncAdvancedSettings? settings,
        bool honorRecycleBin = false)
    {
        _settings = settings?.Clone() ?? new CloudSyncAdvancedSettings();
        HonorRecycleBin = honorRecycleBin;
    }

    /// <summary>
    /// Propagate deletions in two-way mode instead of restoring the missing item.
    /// Mirrors <see cref="CloudSyncAdvancedSettings.SyncDeletions"/>.
    /// </summary>
    public bool SyncDeletions => _settings.SyncDeletions;

    /// <summary>
    /// When a deletion is applied to the local endpoint, route it through the
    /// share's recycle bin so the file stays recoverable. Set from the share's
    /// <c>IsRecycleEnabled</c> flag; the remote endpoint has no Kaimo recycle bin.
    /// </summary>
    public bool HonorRecycleBin { get; }

    public bool ShouldSkip(string name, long size)
    {
        if (_settings.MaxFileSizeBytes is > 0 and var maximum && size > maximum)
            return true;

        string extension = Path.GetExtension(name);
        return extension.Length > 0 && (_settings.ExcludedExtensions ?? []).Contains(extension);
    }

    public Stream LimitUpload(Stream stream) => RateLimitedStream.Wrap(stream, _settings.MaxUploadBytesPerSecond);
    public Stream LimitDownload(Stream stream) => RateLimitedStream.Wrap(stream, _settings.MaxDownloadBytesPerSecond);
}
