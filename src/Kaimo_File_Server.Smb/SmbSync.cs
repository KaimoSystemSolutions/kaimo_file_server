namespace Kaimo_File_Server.Smb;

/// <summary>
/// Async→sync bridge. The SMB library's <c>IFileStore</c> surface is synchronous, while Kaimo's
/// <c>IFileService</c>/<c>IFileSession</c> are async. Everything crosses here — one place, not many —
/// so the bridging strategy stays consistent (and replaceable if the library ever goes async).
/// </summary>
internal static class SmbSync
{
    public static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
    public static T Run<T>(Func<ValueTask<T>> f) => Task.Run(async () => await f()).GetAwaiter().GetResult();
    public static void Run(Func<Task> f) => Task.Run(f).GetAwaiter().GetResult();
    public static void Run(Func<ValueTask> f) => Task.Run(async () => await f()).GetAwaiter().GetResult();
}
