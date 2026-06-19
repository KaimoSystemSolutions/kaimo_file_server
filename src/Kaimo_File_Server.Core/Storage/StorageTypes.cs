using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Storage
{
    public enum OpenMode
    {
        Open,             // SMB FILE_OPEN — fail if not exists
        OpenOrCreate,     // SMB FILE_OPEN_IF
        Create,           // SMB FILE_CREATE — fail if exists
        Truncate,         // SMB FILE_OVERWRITE — fail if not exists, truncate
        CreateOrTruncate, // SMB FILE_OVERWRITE_IF
        Supersede         // SMB FILE_SUPERSEDE
    }

    [Flags]
    public enum AccessIntent
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write
    }

    [Flags]
    public enum ShareIntent
    {
        None = 0,
        Read = 1,
        Write = 2,
        Delete = 4
    }

    public readonly record struct FileTimes(
        DateTime? Created,
        DateTime? LastWritten,
        DateTime? LastAccessed);

    public enum FileOpenStatus
    {
        Created,
        Opened,
        Overwritten,
        Superseded
    }
}
