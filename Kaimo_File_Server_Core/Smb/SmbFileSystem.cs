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
            public FileStream? Stream;
            public string Path = string.Empty;
            public bool IsDirectory;
            public bool DeleteOnClose;
        }

        // ===================== CREATE =====================
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
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;

            try
            {
                string fullPath = GetFullPath(path);
                bool isDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;

                // Auch ohne FILE_DIRECTORY_FILE: wenn der Pfad ein existierendes Verzeichnis ist,
                // behandle es als Directory (Windows Explorer macht das z.B. fuer Root "\")
                if (!isDirectory && Directory.Exists(fullPath))
                    isDirectory = true;

                // ----- DIRECTORY -----
                if (isDirectory)
                {
                    if (createDisposition == CreateDisposition.FILE_CREATE)
                    {
                        if (Directory.Exists(fullPath))
                        {
                            fileStatus = FileStatus.FILE_EXISTS;
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                        }

                        Directory.CreateDirectory(fullPath);
                        fileStatus = FileStatus.FILE_CREATED;
                    }
                    else if (createDisposition == CreateDisposition.FILE_OPEN)
                    {
                        if (!Directory.Exists(fullPath))
                        {
                            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
                            return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
                        }

                        fileStatus = FileStatus.FILE_OPENED;
                    }
                    else
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
                    }

                    handle = new FileHandle { Path = fullPath, IsDirectory = true };
                    return NTStatus.STATUS_SUCCESS;
                }

                // ----- FILE -----

                // Elternverzeichnis pruefen
                string? parentDir = Path.GetDirectoryName(fullPath);
                if (parentDir != null && !Directory.Exists(parentDir))
                    return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;

                bool exists = File.Exists(fullPath);

                // CreateDisposition-Pruefung
                switch (createDisposition)
                {
                    case CreateDisposition.FILE_OPEN:
                    case CreateDisposition.FILE_OVERWRITE:
                        if (!exists)
                            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
                        break;
                    case CreateDisposition.FILE_CREATE:
                        if (exists)
                        {
                            fileStatus = FileStatus.FILE_EXISTS;
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                        }
                        break;
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

                // FileAccess basierend auf desiredAccess ableiten
                FileAccess fileAccess = MapFileAccess(desiredAccess);
                FileShare fileShare = MapFileShare(shareAccess);

                var fs = new FileStream(fullPath, mode, fileAccess, fileShare);

                // FileStatus korrekt setzen
                if (createDisposition == CreateDisposition.FILE_SUPERSEDE && exists)
                    fileStatus = FileStatus.FILE_SUPERSEDED;
                else if ((createDisposition == CreateDisposition.FILE_OVERWRITE ||
                          createDisposition == CreateDisposition.FILE_OVERWRITE_IF) && exists)
                    fileStatus = FileStatus.FILE_OVERWRITTEN;
                else if (!exists)
                    fileStatus = FileStatus.FILE_CREATED;
                else
                    fileStatus = FileStatus.FILE_OPENED;

                handle = new FileHandle
                {
                    Stream = fs,
                    Path = fullPath,
                    IsDirectory = false
                };

                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // Sharing violation
            {
                return NTStatus.STATUS_SHARING_VIOLATION;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CreateFile ERROR] {path}: {ex.Message}");
                return NTStatus.STATUS_ACCESS_DENIED;
            }
        }

        private static FileAccess MapFileAccess(AccessMask desiredAccess)
        {
            bool read = (desiredAccess & (AccessMask.GENERIC_READ
                                         | AccessMask.GENERIC_ALL
                                         | (AccessMask)FileAccessMask.FILE_READ_DATA
                                         | (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES
                                         | (AccessMask)FileAccessMask.FILE_READ_EA)) != 0;

            bool write = (desiredAccess & (AccessMask.GENERIC_WRITE
                                          | AccessMask.GENERIC_ALL
                                          | (AccessMask)FileAccessMask.FILE_WRITE_DATA
                                          | (AccessMask)FileAccessMask.FILE_APPEND_DATA
                                          | (AccessMask)FileAccessMask.FILE_WRITE_ATTRIBUTES
                                          | (AccessMask)FileAccessMask.FILE_WRITE_EA)) != 0;

            if (read && write) return FileAccess.ReadWrite;
            if (write) return FileAccess.ReadWrite;
            return FileAccess.Read;
        }

        private static FileShare MapFileShare(ShareAccess shareAccess)
        {
            FileShare result = FileShare.None;
            if ((shareAccess & ShareAccess.Read) != 0) result |= FileShare.Read;
            if ((shareAccess & ShareAccess.Write) != 0) result |= FileShare.Write;
            if ((shareAccess & ShareAccess.Delete) != 0) result |= FileShare.Delete;
            return result;
        }

        // ===================== READ / WRITE =====================
        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            data = null!;
            var h = handle as FileHandle;

            if (h == null || h.IsDirectory)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                var user = _userContextAccessor.Get();
                if (user == null || !_fileService.CanReadSync(h.Path, user))
                    return NTStatus.STATUS_ACCESS_DENIED;

                h.Stream!.Position = offset;
                byte[] buffer = new byte[maxCount];
                int read = h.Stream.Read(buffer, 0, maxCount);

                if (read == 0)
                {
                    data = Array.Empty<byte>();
                    return NTStatus.STATUS_END_OF_FILE;
                }

                if (read < maxCount)
                    Array.Resize(ref buffer, read);

                data = buffer;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReadFile ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
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
                if (user == null || !_fileService.CanWrite(h.Path, user).GetAwaiter().GetResult())
                    return NTStatus.STATUS_ACCESS_DENIED;

                h.Stream!.Position = offset;
                h.Stream.Write(data, 0, data.Length);
                h.Stream.Flush();
                numberOfBytesWritten = data.Length;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        // ===================== CLOSE =====================
        public NTStatus CloseFile(object handle)
        {
            var h = handle as FileHandle;
            if (h == null)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                h.Stream?.Dispose();

                if (h.DeleteOnClose)
                {
                    if (h.IsDirectory && Directory.Exists(h.Path))
                        Directory.Delete(h.Path, true);
                    else if (!h.IsDirectory && File.Exists(h.Path))
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
            if (h?.Stream == null)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                h.Stream.Flush(true);
                return NTStatus.STATUS_SUCCESS;
            }
            catch
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        // ===================== DIRECTORY LISTING =====================
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
                var dirInfo = new DirectoryInfo(h.Path);
                if (!dirInfo.Exists)
                    return NTStatus.STATUS_NO_SUCH_FILE;

                string pattern = string.IsNullOrEmpty(fileName) ? "*" : fileName;
                bool isWildcard = pattern == "*" || pattern == "*.*";

                // "." und ".." Pseudo-Eintraege – Windows erwartet diese bei Wildcard-Abfragen
                if (isWildcard)
                {
                    result.Add(CreateFileInfoFromDir(".", dirInfo, informationClass));
                    result.Add(CreateFileInfoFromDir("..", dirInfo.Parent ?? dirInfo, informationClass));
                }

                // Verzeichnisse
                foreach (var sub in dirInfo.GetDirectories())
                {
                    if (MatchesPattern(sub.Name, pattern))
                        result.Add(CreateFileInfo(sub.Name, sub, true, informationClass));
                }

                // Dateien
                foreach (var file in dirInfo.GetFiles())
                {
                    if (MatchesPattern(file.Name, pattern))
                        result.Add(CreateFileInfo(file.Name, file, false, informationClass));
                }

                // Spezifisches Pattern gesucht, aber nichts gefunden
                if (result.Count == 0)
                    return NTStatus.STATUS_NO_SUCH_FILE;

                return NTStatus.STATUS_SUCCESS;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QueryDirectory ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        /// <summary>
        /// Einfacher Wildcard-Matcher fuer SMB-Patterns (* und ?)
        /// Behandelt auch das spezielle DOS-Pattern "*.*" als "alles"
        /// </summary>
        private static bool MatchesPattern(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*")
                return true;

            string regexPattern = "^" +
                System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") +
                "$";

            return System.Text.RegularExpressions.Regex.IsMatch(
                name, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private QueryDirectoryFileInformation CreateFileInfoFromDir(
            string name,
            DirectoryInfo dirInfo,
            FileInformationClass informationClass)
        {
            return informationClass switch
            {
                FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory
                },
                FileInformationClass.FileFullDirectoryInformation => new FileFullDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory,
                    EaSize = 0
                },
                FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
                {
                    FileName = name,
                    ShortName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory,
                    EaSize = 0
                },
                FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
                {
                    FileName = name,
                    ShortName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory,
                    EaSize = 0,
                    FileId = 0
                },
                FileInformationClass.FileIdFullDirectoryInformation => new FileIdFullDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory,
                    EaSize = 0,
                    FileId = 0
                },
                FileInformationClass.FileNamesInformation => new FileNamesInformation
                {
                    FileName = name
                },
                _ => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = dirInfo.CreationTimeUtc,
                    LastAccessTime = dirInfo.LastAccessTimeUtc,
                    LastWriteTime = dirInfo.LastWriteTimeUtc,
                    ChangeTime = dirInfo.LastWriteTimeUtc,
                    EndOfFile = 0,
                    AllocationSize = 0,
                    FileAttributes = FileAttributes.Directory
                }
            };
        }

        private QueryDirectoryFileInformation CreateFileInfo(
            string name,
            FileSystemInfo info,
            bool isDirectory,
            FileInformationClass informationClass)
        {
            var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            long size = isDirectory ? 0 : ((FileInfo)info).Length;
            long allocSize = RoundUpAllocation(size);

            return informationClass switch
            {
                FileInformationClass.FileDirectoryInformation => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs
                },
                FileInformationClass.FileFullDirectoryInformation => new FileFullDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs,
                    EaSize = 0
                },
                FileInformationClass.FileBothDirectoryInformation => new FileBothDirectoryInformation
                {
                    FileName = name,
                    ShortName = GenerateShortName(name),
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs,
                    EaSize = 0
                },
                FileInformationClass.FileIdBothDirectoryInformation => new FileIdBothDirectoryInformation
                {
                    FileName = name,
                    ShortName = GenerateShortName(name),
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs,
                    EaSize = 0,
                    FileId = 0
                },
                FileInformationClass.FileIdFullDirectoryInformation => new FileIdFullDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs,
                    EaSize = 0,
                    FileId = 0
                },
                FileInformationClass.FileNamesInformation => new FileNamesInformation
                {
                    FileName = name
                },
                _ => new FileDirectoryInformation
                {
                    FileName = name,
                    CreationTime = info.CreationTimeUtc,
                    LastAccessTime = info.LastAccessTimeUtc,
                    LastWriteTime = info.LastWriteTimeUtc,
                    ChangeTime = info.LastWriteTimeUtc,
                    EndOfFile = size,
                    AllocationSize = allocSize,
                    FileAttributes = attrs
                }
            };
        }

        private static string GenerateShortName(string name)
        {
            if (name.Length <= 12) return name;

            string ext = Path.GetExtension(name);
            string baseName = Path.GetFileNameWithoutExtension(name);

            if (ext.Length > 4) ext = ext.Substring(0, 4);
            int baseLen = Math.Min(baseName.Length, 6);
            return baseName.Substring(0, baseLen).ToUpperInvariant() + "~1" + ext.ToUpperInvariant();
        }

        private static long RoundUpAllocation(long size)
        {
            const long clusterSize = 4096;
            if (size == 0) return 0;
            return ((size + clusterSize - 1) / clusterSize) * clusterSize;
        }

        private static FileStreamInformation CreateFileStreamInformation(long fileSize, long allocSize)
        {
            var info = new FileStreamInformation();
            var entry = new FileStreamEntry();
            entry.StreamName = "::$DATA";
            entry.StreamSize = fileSize;
            entry.StreamAllocationSize = allocSize;
            info.Entries.Add(entry);
            return info;
        }

        // ===================== FILE INFO =====================
        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            result = null!;
            var h = handle as FileHandle;

            if (h == null)
                return NTStatus.STATUS_INVALID_HANDLE;

            try
            {
                if (h.IsDirectory)
                {
                    var d = new DirectoryInfo(h.Path);
                    if (!d.Exists)
                        return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

                    result = informationClass switch
                    {
                        FileInformationClass.FileBasicInformation => new FileBasicInformation
                        {
                            CreationTime = d.CreationTimeUtc,
                            LastWriteTime = d.LastWriteTimeUtc,
                            LastAccessTime = d.LastAccessTimeUtc,
                            ChangeTime = d.LastWriteTimeUtc,
                            FileAttributes = FileAttributes.Directory
                        },
                        FileInformationClass.FileStandardInformation => new FileStandardInformation
                        {
                            AllocationSize = 0,
                            EndOfFile = 0,
                            NumberOfLinks = 1,
                            DeletePending = h.DeleteOnClose,
                            Directory = true
                        },
                        FileInformationClass.FileInternalInformation => new FileInternalInformation
                        {
                            IndexNumber = 0
                        },
                        FileInformationClass.FileEaInformation => new FileEaInformation
                        {
                            EaSize = 0
                        },
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
                        FileInformationClass.FileAttributeTagInformation => new FileAttributeTagInformation
                        {
                            FileAttributes = FileAttributes.Directory,
                            ReparsePointTag = 0
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
                    if (!f.Exists)
                        return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;

                    long fileSize = f.Length;
                    long allocSize = RoundUpAllocation(fileSize);

                    result = informationClass switch
                    {
                        FileInformationClass.FileBasicInformation => new FileBasicInformation
                        {
                            CreationTime = f.CreationTimeUtc,
                            LastWriteTime = f.LastWriteTimeUtc,
                            LastAccessTime = f.LastAccessTimeUtc,
                            ChangeTime = f.LastWriteTimeUtc,
                            FileAttributes = FileAttributes.Normal
                        },
                        FileInformationClass.FileStandardInformation => new FileStandardInformation
                        {
                            AllocationSize = allocSize,
                            EndOfFile = fileSize,
                            NumberOfLinks = 1,
                            DeletePending = h.DeleteOnClose,
                            Directory = false
                        },
                        FileInformationClass.FileInternalInformation => new FileInternalInformation
                        {
                            IndexNumber = 0
                        },
                        FileInformationClass.FileEaInformation => new FileEaInformation
                        {
                            EaSize = 0
                        },
                        FileInformationClass.FileNetworkOpenInformation => new FileNetworkOpenInformation
                        {
                            CreationTime = f.CreationTimeUtc,
                            LastWriteTime = f.LastWriteTimeUtc,
                            LastAccessTime = f.LastAccessTimeUtc,
                            ChangeTime = f.LastWriteTimeUtc,
                            AllocationSize = allocSize,
                            EndOfFile = fileSize,
                            FileAttributes = FileAttributes.Normal
                        },
                        FileInformationClass.FileAttributeTagInformation => new FileAttributeTagInformation
                        {
                            FileAttributes = FileAttributes.Normal,
                            ReparsePointTag = 0
                        },
                        FileInformationClass.FileStreamInformation => CreateFileStreamInformation(fileSize, allocSize),
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
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        // ===================== SET FILE INFO (RENAME + DELETE + TIMESTAMPS) =====================
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
                    h.DeleteOnClose = disposition.DeletePending;
                    return NTStatus.STATUS_SUCCESS;
                }

                // RENAME
                if (information is FileRenameInformationType2 rename)
                {
                    string newPath = GetFullPath(rename.FileName);

                    if (h.IsDirectory)
                    {
                        if (Directory.Exists(newPath))
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;

                        Directory.Move(h.Path, newPath);
                    }
                    else
                    {
                        h.Stream?.Dispose();
                        h.Stream = null;

                        if (File.Exists(newPath) && !rename.ReplaceIfExists)
                            return NTStatus.STATUS_OBJECT_NAME_COLLISION;

                        if (File.Exists(newPath) && rename.ReplaceIfExists)
                            File.Delete(newPath);

                        File.Move(h.Path, newPath);
                        h.Stream = new FileStream(newPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }

                    h.Path = newPath;
                    return NTStatus.STATUS_SUCCESS;
                }

                // SET BASIC INFO (Timestamps, Attribute)
                if (information is FileBasicInformation basicInfo)
                {
                    if (h.IsDirectory)
                    {
                        var d = new DirectoryInfo(h.Path);
                        if (basicInfo.CreationTime.Time.HasValue && basicInfo.CreationTime.Time.Value > DateTime.MinValue)
                            d.CreationTimeUtc = basicInfo.CreationTime.Time.Value;
                        if (basicInfo.LastWriteTime.Time.HasValue && basicInfo.LastWriteTime.Time.Value > DateTime.MinValue)
                            d.LastWriteTimeUtc = basicInfo.LastWriteTime.Time.Value;
                        if (basicInfo.LastAccessTime.Time.HasValue && basicInfo.LastAccessTime.Time.Value > DateTime.MinValue)
                            d.LastAccessTimeUtc = basicInfo.LastAccessTime.Time.Value;
                    }
                    else
                    {
                        var f = new FileInfo(h.Path);
                        if (basicInfo.CreationTime.Time.HasValue && basicInfo.CreationTime.Time.Value > DateTime.MinValue)
                            f.CreationTimeUtc = basicInfo.CreationTime.Time.Value;
                        if (basicInfo.LastWriteTime.Time.HasValue && basicInfo.LastWriteTime.Time.Value > DateTime.MinValue)
                            f.LastWriteTimeUtc = basicInfo.LastWriteTime.Time.Value;
                        if (basicInfo.LastAccessTime.Time.HasValue && basicInfo.LastAccessTime.Time.Value > DateTime.MinValue)
                            f.LastAccessTimeUtc = basicInfo.LastAccessTime.Time.Value;
                    }

                    return NTStatus.STATUS_SUCCESS;
                }

                // END OF FILE (Truncate/Extend)
                if (information is FileEndOfFileInformation eofInfo)
                {
                    if (h.Stream != null)
                        h.Stream.SetLength(eofInfo.EndOfFile);
                    return NTStatus.STATUS_SUCCESS;
                }

                // ALLOCATION SIZE
                if (information is FileAllocationInformation allocInfo)
                {
                    if (h.Stream != null && h.Stream.Length > allocInfo.AllocationSize)
                        h.Stream.SetLength(allocInfo.AllocationSize);
                    return NTStatus.STATUS_SUCCESS;
                }

                return NTStatus.STATUS_NOT_SUPPORTED;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_SHARING_VIOLATION;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SetFileInformation ERROR] {ex.Message}");
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        // ===================== FILESYSTEM INFO =====================
        public NTStatus GetFileSystemInformation(
            out FileSystemInformation result,
            FileSystemInformationClass informationClass)
        {
            result = null!;

            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_root) ?? _root);

                switch (informationClass)
                {
                    case FileSystemInformationClass.FileFsVolumeInformation:
                        result = new FileFsVolumeInformation
                        {
                            VolumeLabel = "KaimoSMB",
                            VolumeSerialNumber = 0x12345678
                        };
                        return NTStatus.STATUS_SUCCESS;

                    case FileSystemInformationClass.FileFsSizeInformation:
                        result = new FileFsSizeInformation
                        {
                            TotalAllocationUnits = driveInfo.TotalSize / 4096,
                            AvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            SectorsPerAllocationUnit = 8,
                            BytesPerSector = 512
                        };
                        return NTStatus.STATUS_SUCCESS;

                    case FileSystemInformationClass.FileFsFullSizeInformation:
                        result = new FileFsFullSizeInformation
                        {
                            TotalAllocationUnits = driveInfo.TotalSize / 4096,
                            CallerAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            ActualAvailableAllocationUnits = driveInfo.AvailableFreeSpace / 4096,
                            SectorsPerAllocationUnit = 8,
                            BytesPerSector = 512
                        };
                        return NTStatus.STATUS_SUCCESS;

                    case FileSystemInformationClass.FileFsDeviceInformation:
                        result = new FileFsDeviceInformation
                        {
                            DeviceType = DeviceType.Disk,
                            Characteristics = (DeviceCharacteristics)0
                        };
                        return NTStatus.STATUS_SUCCESS;

                    case FileSystemInformationClass.FileFsAttributeInformation:
                        result = new FileFsAttributeInformation
                        {
                            FileSystemAttributes =
                                FileSystemAttributes.UnicodeOnDisk |
                                FileSystemAttributes.CasePreservedNames,
                            MaximumComponentNameLength = 255,
                            FileSystemName = "NTFS"
                        };
                        return NTStatus.STATUS_SUCCESS;

                    default:
                        return NTStatus.STATUS_INVALID_PARAMETER;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetFileSystemInformation ERROR] {ex.Message}");
                result = new FileFsVolumeInformation { VolumeLabel = "KaimoSMB" };
                return NTStatus.STATUS_SUCCESS;
            }
        }

        // ===================== SECURITY =====================
        public NTStatus GetSecurityInformation(
            out SecurityDescriptor result,
            object handle,
            SecurityInformation securityInformation)
        {
            // Minimaler SecurityDescriptor damit Windows Explorer nicht abbricht
            result = new SecurityDescriptor();
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus SetSecurityInformation(
            object handle,
            SecurityInformation securityInformation,
            SecurityDescriptor securityDescriptor)
        {
            // Akzeptieren aber ignorieren – wir nutzen eigene ACLs
            return NTStatus.STATUS_SUCCESS;
        }

        // ===================== STUBS =====================
        public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_SUCCESS;

        public NTStatus DeviceIOControl(
            object handle, uint ctlCode, byte[] input,
            out byte[] output, int maxOutputLength)
        {
            output = null!;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
            => NTStatus.STATUS_SUCCESS; // Akzeptieren, nicht erzwingen

        public NTStatus UnlockFile(object handle, long byteOffset, long length)
            => NTStatus.STATUS_SUCCESS;

        public NTStatus NotifyChange(
            out object ioRequest, object handle,
            NotifyChangeFilter completionFilter, bool watchTree,
            int outputBufferSize,
            OnNotifyChangeCompleted onNotifyChangeCompleted,
            object context)
        {
            ioRequest = null!;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus SetFileSystemInformation(FileSystemInformation information)
            => NTStatus.STATUS_NOT_SUPPORTED;
    }
}