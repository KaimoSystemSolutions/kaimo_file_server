using Kaimo_File_Server.Core.Storage;

namespace Kaimo_File_Server.Infrastructure.Storage;

public partial class FileSystemStorage
{
    private sealed class FileSystemStorageHandle : IStorageHandle
    {
        private FileStream? _stream;
        private bool _disposed;

        // Remembered so the stream can be reopened identically after a move.
        private readonly FileAccess _access;
        private readonly FileShare _share;

        public string RelativePath { get; private set; }
        public string AbsolutePath { get; private set; }
        public bool IsDirectory { get; }
        public long Length => _stream?.Length ?? 0;
        public bool IsDirty { get; private set; }
        public bool DeleteOnClose { get; private set; }

        public FileSystemStorageHandle(
            FileStream? stream, string absolutePath, string relativePath, bool isDirectory,
            FileAccess access = FileAccess.Read, FileShare share = FileShare.None)
        {
            _stream = stream;
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            _access = access;
            _share = share;
        }

        public async ValueTask MoveAsync(
            string newAbsolutePath, string newRelativePath,
            bool replaceExisting, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSystemStorageHandle));

            if (string.Equals(AbsolutePath, newAbsolutePath, StringComparison.Ordinal))
                return;

            if (IsDirectory)
            {
                if (Directory.Exists(newAbsolutePath) || File.Exists(newAbsolutePath))
                {
                    if (!replaceExisting)
                        throw new IOException($"Target already exists: '{newAbsolutePath}'.");
                    if (Directory.Exists(newAbsolutePath)) Directory.Delete(newAbsolutePath, true);
                    else File.Delete(newAbsolutePath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolutePath)!);
                Directory.Move(AbsolutePath, newAbsolutePath);
                AbsolutePath = newAbsolutePath;
                RelativePath = newRelativePath;
                return;
            }

            // File: close → move → reopen so the handle stays usable.
            long position = _stream?.Position ?? 0;
            if (_stream != null) await _stream.DisposeAsync();

            try
            {
                if (File.Exists(newAbsolutePath) || Directory.Exists(newAbsolutePath))
                {
                    if (!replaceExisting)
                        throw new IOException($"Target already exists: '{newAbsolutePath}'.");
                    if (File.Exists(newAbsolutePath)) File.Delete(newAbsolutePath);
                    else Directory.Delete(newAbsolutePath, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolutePath)!);
                File.Move(AbsolutePath, newAbsolutePath);
                AbsolutePath = newAbsolutePath;
                RelativePath = newRelativePath;
            }
            catch
            {
                // Reopen the source so the caller's handle remains usable on failure.
                if (File.Exists(AbsolutePath))
                {
                    _stream = new FileStream(AbsolutePath, FileMode.Open, _access, _share,
                                             bufferSize: 4096, useAsync: true);
                    if (position > 0) _stream.Position = Math.Min(position, _stream.Length);
                }
                throw;
            }

            _stream = new FileStream(AbsolutePath, FileMode.Open, _access, _share,
                                     bufferSize: 4096, useAsync: true);
            if (position > 0) _stream.Position = Math.Min(position, _stream.Length);
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Directory handle is not readable.");
            if (_stream.Position != offset) _stream.Position = offset;
            return await _stream.ReadAsync(buffer, ct);
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("Directory handle is not writable.");
            if (_stream.Position != offset) _stream.Position = offset;
            await _stream.WriteAsync(data, ct);
            IsDirty = true;
        }

        public ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException();
            _stream.SetLength(length);
            IsDirty = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTimesAsync(FileTimes times, CancellationToken ct)
        {
            // Linux has no API to set the birth time; .NET silently writes mtime instead,
            // which would clobber the modified date. Store it where Samba and the web read it.
            if (OperatingSystem.IsLinux())
            {
                FileSystemInfo info = IsDirectory ? new DirectoryInfo(AbsolutePath) : new FileInfo(AbsolutePath);
                if (times.Created is { } created && created > DateTime.MinValue)
                    FileBirthTime.TrySetUtc(info, created);
                else if (times.LastWritten is not null)
                    FileBirthTime.PinBeforeTimeChange(info);
                times = times with { Created = null };
            }

            if (IsDirectory)
            {
                var d = new DirectoryInfo(AbsolutePath);
                if (times.Created is { } c && c > DateTime.MinValue) d.CreationTimeUtc = c;
                if (times.LastWritten is { } w && w > DateTime.MinValue) d.LastWriteTimeUtc = w;
                if (times.LastAccessed is { } a && a > DateTime.MinValue) d.LastAccessTimeUtc = a;
            }
            else
            {
                var f = new FileInfo(AbsolutePath);
                if (times.Created is { } c && c > DateTime.MinValue) f.CreationTimeUtc = c;
                if (times.LastWritten is { } w && w > DateTime.MinValue) f.LastWriteTimeUtc = w;
                if (times.LastAccessed is { } a && a > DateTime.MinValue) f.LastAccessTimeUtc = a;
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_stream != null) await _stream.FlushAsync(ct);
        }

        public Stream? GetReadableSnapshot()
        {
            if (_stream == null || !_stream.CanRead) return null;
            _stream.Flush(true);
            _stream.Position = 0;
            return _stream;
        }

        public void MarkDeleteOnClose() => DeleteOnClose = true;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_stream != null)
                await _stream.DisposeAsync();

            if (DeleteOnClose)
            {
                try
                {
                    if (IsDirectory && Directory.Exists(AbsolutePath))
                        Directory.Delete(AbsolutePath, recursive: true);
                    else if (!IsDirectory && File.Exists(AbsolutePath))
                        File.Delete(AbsolutePath);
                }
                catch { /* permission was validated at Open */ }
            }
        }
    }
}
