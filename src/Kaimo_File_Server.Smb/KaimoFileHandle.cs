using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Smb.FileSystem;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// <see cref="IFileHandle"/> over a Kaimo <see cref="IFileSession"/>. The session captured the
/// authenticated <see cref="Core.Domain.Identity.UserContext"/> at open time and carries it through
/// every operation, so the handle itself is the identity source for READ/WRITE/QUERY_INFO/SET_INFO/CLOSE
/// (only CREATE needs the ambient caller).
/// </summary>
internal sealed class KaimoFileHandle : IFileHandle
{
    private readonly IFileService _fileService;

    public KaimoFileHandle(IFileService fileService, IFileSession session)
    {
        _fileService = fileService;
        Session = session;
    }

    public IFileSession Session { get; }

    public string Path => Session.RelativePath;
    public bool IsDirectory => Session.IsDirectory;
    public string? PhysicalPath => Session.AbsolutePath;

    public FileEntryInfo GetInfo()
    {
        try
        {
            FileMetadata meta = SmbSync.Run(() => _fileService.GetMetadataAsync(Session.RelativePath, Session.User));
            // The open handle is authoritative for the current size — on-disk metadata can lag an
            // in-progress write until it is flushed.
            long size = Session.IsDirectory ? 0 : Session.Length;
            return KaimoFileInfo.From(meta, size);
        }
        catch
        {
            // Metadata may not be queryable yet (e.g. immediately after CREATE) — synthesize so the
            // CREATE response can still be built instead of failing a successful open.
            string name = System.IO.Path.GetFileName(Session.RelativePath.Replace('\\', '/'));
            return KaimoFileInfo.Synthesize(name, Session.IsDirectory, Session.IsDirectory ? 0 : Session.Length);
        }
    }

    public void Dispose() => SmbSync.Run(() => Session.DisposeAsync());
}
