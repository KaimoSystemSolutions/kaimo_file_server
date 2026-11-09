namespace Kaimo_File_Server.Core.Helpers;

public static class StorageErrors
{
    /// <summary>ENOSPC / EDQUOT (.NET puts the raw errno into HResult on Unix), or the Win32 disk-full HRESULTs.</summary>
    public static bool IsDiskFull(IOException exception)
        => exception.HResult is 28 or 122 or unchecked((int)0x80070070) or unchecked((int)0x80070027);
}
