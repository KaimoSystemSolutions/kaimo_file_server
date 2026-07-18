using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC-Fassade für die Close-/Event-Hooks (Phase 3). Samba führt die Datei-I/O
/// nativ aus und meldet danach das Ereignis; hier laufen die Cross-Cutting-Effekte
/// (Versionierung, Suchindex, Ownership) über eine per <see cref="IFileServiceFactory"/>
/// erzeugte <see cref="IFileService"/> — dieselbe Verdrahtung wie im Host, also
/// identische Version-/Index-/Ownership-Ergebnisse wie bei Web-Uploads.
/// </summary>
public sealed class FileEventGrpcService : EventService.EventServiceBase
{
    private readonly IFileServiceFactory _factory;
    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly ILogger<FileEventGrpcService> _logger;

    public FileEventGrpcService(
        IFileServiceFactory factory,
        IShareRepository shares,
        IAuthenticationLookup auth,
        ILogger<FileEventGrpcService> logger)
    {
        _factory = factory;
        _shares = shares;
        _auth = auth;
        _logger = logger;
    }

    public override async Task<NotifyReply> NotifyClose(NotifyCloseRequest request, ServerCallContext context)
    {
        var (svc, user) = await ResolveAsync(request.Username, request.Share);
        if (svc is null || user is null) return Fail();

        await svc.NotifyExternalCloseAsync(request.Path, user);
        _logger.LogInformation("NotifyClose: share={Share} path=[{Path}] user={User}",
            request.Share, request.Path, request.Username);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyMkdir(NotifyPathRequest request, ServerCallContext context)
    {
        var (svc, user) = await ResolveAsync(request.Username, request.Share);
        if (svc is null || user is null) return Fail();

        await svc.NotifyExternalMkdirAsync(request.Path, user);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyDelete(NotifyPathRequest request, ServerCallContext context)
    {
        var svc = await ResolveServiceAsync(request.Share);
        if (svc is null) return Fail();

        await svc.NotifyExternalDeleteAsync(request.Path, request.IsDirectory);
        _logger.LogInformation("NotifyDelete: share={Share} path=[{Path}] dir={Dir}",
            request.Share, request.Path, request.IsDirectory);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyRename(NotifyRenameRequest request, ServerCallContext context)
    {
        var svc = await ResolveServiceAsync(request.Share);
        if (svc is null) return Fail();

        await svc.NotifyExternalRenameAsync(request.OldPath, request.NewPath, request.IsDirectory);
        _logger.LogInformation("NotifyRename: share={Share} [{Old}] -> [{New}] dir={Dir}",
            request.Share, request.OldPath, request.NewPath, request.IsDirectory);
        return Ok();
    }

    // ---- helpers ----

    private async Task<(IFileService? Service, UserContext? User)> ResolveAsync(string username, string share)
    {
        var user = await _auth.ResolveUserContextAsync(username);
        var svc = await ResolveServiceAsync(share);
        return (svc, user);
    }

    private async Task<IFileService?> ResolveServiceAsync(string share)
    {
        var def = await _shares.GetByNameAsync(share);
        if (def is null)
        {
            _logger.LogWarning("Event für unbekannten Share '{Share}' verworfen.", share);
            return null;
        }
        return _factory.CreateForShare(def.Id, def.Path);
    }

    private static NotifyReply Ok() => new() { Ok = true };
    private static NotifyReply Fail() => new() { Ok = false };
}
