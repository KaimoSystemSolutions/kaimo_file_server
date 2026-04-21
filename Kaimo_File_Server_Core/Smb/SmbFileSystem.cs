using Kaimo_File_Server_Core.Core.Services;
using SMBLibrary;
using SMBLibrary.Server;
using FileAttributes = SMBLibrary.FileAttributes;
namespace Kaimo_File_Server_Core.Smb

{
    public class SmbFileSystem : INTFileStore
    {
        private readonly string _root;
        private readonly FileService _fileService;
        private readonly UserContextAccessor _userContextAccessor;

        public SmbFileSystem(string rootPath, FileService fileService, UserContextAccessor userContextAccessor)
        {
            _root = rootPath;
            _fileService = fileService;
            _userContextAccessor = userContextAccessor;
            Directory.CreateDirectory(_root);
        }

        private string GetFullPath(string path)
        {
            path = path.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(_root, path);
        }

        private class FileHandle
        {
            public FileStream Stream;
            public string Path;
            public bool IsDirectory;
            public bool DeleteOnClose;
        }

        // ---------------- CREATE ----------------
        public NTStatus CreateFile(
            out object handle,
            out FileStatus fileStatus,
            string path,
            AccessMask desiredAccess,
            FileAttributes fileAttributes,
            ShareAccess shareAccess,
            CreateDisposition createDisposition,
            CreateOptions createOptions,
            SecurityContext securityContext)
        {
            handle = null;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            try
            {
                string fullPath = GetFullPath(path);
                bool isDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;

                if (isDirectory)
                {
                    if (!Directory.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                        fileStatus = FileStatus.FILE_CREATED;
                    }
                    else
                    {
                        fileStatus = FileStatus.FILE_OPENED;
                    }

                    handle = new FileHandle { Path = fullPath, IsDirectory = true };
                    return NTStatus.STATUS_SUCCESS;
                }

                FileMode mode = createDisposition switch
                {
                    CreateDisposition.FILE_CREATE => FileMode.CreateNew,
                    CreateDisposition.FILE_OPEN => FileMode.Open,
                    CreateDisposition.FILE_OPEN_IF => FileMode.OpenOrCreate,
                    CreateDisposition.FILE_OVERWRITE => FileMode.Truncate,
                    CreateDisposition.FILE_OVERWRITE_IF => FileMode.Create,
                    CreateDisposition.FILE_SUPERSEDE => FileMode.Create,
                    _ => FileMode.OpenOrCreate
                };

                bool exists = File.Exists(fullPath);

                var fs = new FileStream(fullPath, mode, FileAccess.ReadWrite, FileShare.ReadWrite);

                fileStatus = exists ? FileStatus.FILE_OPENED : FileStatus.FILE_CREATED;

                handle = new FileHandle
                {
                    Stream = fs,
                    Path = fullPath,
                    IsDirectory = false
                };

                return NTStatus.STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CreateFile ERROR] {path}: {ex.Message}");
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        // ---------------- READ / WRITE ----------------
        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            data = null;
            var h = handle as FileHandle;

            if (h == null || h.IsDirectory)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                h.Stream.Position = offset;
                byte[] buffer = new byte[maxCount];
                int read = h.Stream.Read(buffer, 0, maxCount);

                if (read < maxCount)
                    Array.Resize(ref buffer, read);

                data = buffer;
                return NTStatus.STATUS_SUCCESS;
            }
            catch
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
        {
            numberOfBytesWritten = 0;
            var h = handle as FileHandle;

            if (h == null || h.IsDirectory)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                var user = _userContextAccessor.Get();
                var stream = new MemoryStream(data);
                _fileService.WriteFile(h.Path, stream, user).GetAwaiter().GetResult();
                numberOfBytesWritten = data.Length;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        // ---------------- CLOSE ----------------
        public NTStatus CloseFile(object handle)
        {
            var h = handle as FileHandle;

            try
            {
                h?.Stream?.Dispose();

                if (h?.DeleteOnClose == true)
                {
                    if (h.IsDirectory)
                        Directory.Delete(h.Path, true);
                    else if (File.Exists(h.Path))
                        File.Delete(h.Path);
                }

                return NTStatus.STATUS_SUCCESS;
            }
            catch
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        public NTStatus FlushFileBuffers(object handle)
        {
            var h = handle as FileHandle;

            try
            {
                h?.Stream?.Flush();
                return NTStatus.STATUS_SUCCESS;
            }
            catch
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        // ---------------- DIRECTORY ----------------
        public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle,
        string fileName,
        FileInformationClass informationClass)
        {
            result = new List<QueryDirectoryFileInformation>();

            var h = handle as FileHandle;
            if (h == null || !h.IsDirectory)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                foreach (var dir in Directory.GetDirectories(h.Path))
                {
                    var info = new DirectoryInfo(dir);

                    result.Add(new FileDirectoryInformation
                    {
                        FileName = info.Name,
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        EndOfFile = 0,
                        AllocationSize = 0,
                        FileAttributes = FileAttributes.Directory
                    });
                }

                foreach (var file in Directory.GetFiles(h.Path))
                {
                    var info = new FileInfo(file);

                    result.Add(new FileDirectoryInformation
                    {
                        FileName = info.Name,
                        CreationTime = info.CreationTimeUtc,
                        LastAccessTime = info.LastAccessTimeUtc,
                        LastWriteTime = info.LastWriteTimeUtc,
                        ChangeTime = info.LastWriteTimeUtc,
                        EndOfFile = info.Length,
                        AllocationSize = info.Length,
                        FileAttributes = FileAttributes.Normal
                    });
                }

                return NTStatus.STATUS_SUCCESS;
            }
            catch
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        // ---------------- FILE INFO ----------------
        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            result = null;
            var h = handle as FileHandle;

            if (h == null)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                if (h.IsDirectory)
                {
                    var d = new DirectoryInfo(h.Path);

                    result = informationClass switch
                    {
                        FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                        {
                            CreationTime = d.CreationTimeUtc,
                            LastWriteTime = d.LastWriteTimeUtc,
                            LastAccessTime = d.LastAccessTimeUtc,
                            ChangeTime = d.LastWriteTimeUtc,
                            AllocationSize = 0,
                            EndOfFile = 0,
                            FileAttributes = FileAttributes.Directory
                        },
                        _ => new FileBasicInformation
                        {
                            CreationTime = d.CreationTimeUtc,
                            LastWriteTime = d.LastWriteTimeUtc,
                            LastAccessTime = d.LastAccessTimeUtc,
                            ChangeTime = d.LastWriteTimeUtc,
                            FileAttributes = FileAttributes.Directory
                        }
                    };
                }
                else
                {
                    var f = new FileInfo(h.Path);

                    result = informationClass switch
                    {
                        FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                        {
                            CreationTime = f.CreationTimeUtc,
                            LastWriteTime = f.LastWriteTimeUtc,
                            LastAccessTime = f.LastAccessTimeUtc,
                            ChangeTime = f.LastWriteTimeUtc,
                            AllocationSize = f.Length,
                            EndOfFile = f.Length,
                            FileAttributes = FileAttributes.Normal
                        },
                        _ => new FileBasicInformation
                        {
                            CreationTime = f.CreationTimeUtc,
                            LastWriteTime = f.LastWriteTimeUtc,
                            LastAccessTime = f.LastAccessTimeUtc,
                            ChangeTime = f.LastWriteTimeUtc,
                            FileAttributes = FileAttributes.Normal
                        }
                    };
                }

                return NTStatus.STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetFileInformation ERROR] {ex.Message}");
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }
        // ---------------- RENAME + DELETE ----------------
        public NTStatus SetFileInformation(object handle, FileInformation information)
        {
            var h = handle as FileHandle;
            if (h == null)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                // DELETE
                if (information is FileDispositionInformation disposition)
                {
                    if (disposition.DeletePending)
                    {
                        h.DeleteOnClose = true;
                    }

                    return NTStatus.STATUS_SUCCESS;
                }

                // RENAME
                if (information is FileRenameInformationType2 rename)
                {
                    string newPath = GetFullPath(rename.FileName);

                    if (h.IsDirectory)
                    {
                        Directory.Move(h.Path, newPath);
                    }
                    else
                    {
                        h.Stream.Dispose();
                        File.Move(h.Path, newPath);
                        h.Stream = new FileStream(newPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }

                    h.Path = newPath;
                    return NTStatus.STATUS_SUCCESS;
                }

                return NTStatus.STATUS_NOT_SUPPORTED;
            }
            catch
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        // ---------------- FILESYSTEM INFO ----------------
        public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
        {
            result = new FileFsVolumeInformation
            {
                VolumeLabel = "SMB"
            };

            return NTStatus.STATUS_SUCCESS;
        }

        // ---------------- UNUSED ----------------
        public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;
        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
        {
            output = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation)
        {
            result = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
            => NTStatus.STATUS_NOT_SUPPORTED;

        public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
        {
            ioRequest = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus SetFileSystemInformation(FileSystemInformation information)
            => NTStatus.STATUS_NOT_SUPPORTED;

        public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor)
            => NTStatus.STATUS_NOT_SUPPORTED;

        public NTStatus UnlockFile(object handle, long byteOffset, long length)
            => NTStatus.STATUS_NOT_SUPPORTED;

    }
}