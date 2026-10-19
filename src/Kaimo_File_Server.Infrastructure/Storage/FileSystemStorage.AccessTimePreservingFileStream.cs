namespace Kaimo_File_Server.Infrastructure.Storage;

public partial class FileSystemStorage
{
    /// <summary>
    /// Read stream that puts the file's last-access time back on dispose, so a server-internal
    /// read leaves no trace in the "last accessed" column.
    /// </summary>
    // The restore goes through the still-open handle, so it hits exactly the inode that was read:
    // a file atomically replaced during the read (temp + rename) keeps its own atime, and on Linux
    // no concurrent write's mtime can be reset. A read-only handle on Windows lacks
    // FILE_WRITE_ATTRIBUTES; there (development only) it falls back to the path.
    // Known limit: a user read that overlaps an internal one loses its atime bump. O_NOATIME would
    // avoid the restore entirely but is Linux-only and requires file ownership.
    private sealed class AccessTimePreservingFileStream(string path, DateTime accessedUtc)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true)
    {
        private bool _restored;

        protected override void Dispose(bool disposing)
        {
            // Before base.Dispose, while the handle is open. Never from the finalizer: a stream
            // that was not disposed would otherwise write a stale atime back much later (over a
            // newer sync stamp or user access), on the finalizer thread.
            if (disposing) Restore();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            // base.DisposeAsync calls Dispose(false), which does not restore again.
            Restore();
            await base.DisposeAsync();
        }

        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            try
            {
                File.SetLastAccessTimeUtc(SafeFileHandle, accessedUtc);
                return;
            }
            catch { /* Windows read-only handle: fall back to the path below */ }

            try { File.SetLastAccessTimeUtc(Name, accessedUtc); }
            catch { /* best-effort: the file may have been deleted meanwhile */ }
        }
    }
}
