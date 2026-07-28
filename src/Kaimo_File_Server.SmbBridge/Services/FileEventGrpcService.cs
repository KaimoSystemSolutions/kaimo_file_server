using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for close/event hooks (Phase 3). Samba performs file I/O natively
/// and reports the event afterward; here cross-cutting effects (versioning, search
/// index, ownership) run through an <see cref="IFileService"/> created via
/// <see cref="IFileServiceFactory"/> — same wiring as in the host, so identical
/// version/index/ownership results as web uploads.
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
        var (svc, user) = await ResolveAsync(
            request.Username, request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null || user is null) return Fail();

        await svc.NotifyExternalCloseAsync(request.Path, user);
        _logger.LogInformation("NotifyClose: share={Share} path=[{Path}] user={User}",
            request.Share, request.Path, request.Username);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyMkdir(NotifyPathRequest request, ServerCallContext context)
    {
        var (svc, user) = await ResolveAsync(
            request.Username, request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null || user is null) return Fail();

        await svc.NotifyExternalMkdirAsync(request.Path, user);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyDelete(NotifyPathRequest request, ServerCallContext context)
    {
        var svc = await ResolveServiceAsync(
            request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null) return Fail();

        await svc.NotifyExternalDeleteAsync(request.Path, request.IsDirectory);
        _logger.LogInformation("NotifyDelete: share={Share} path=[{Path}] dir={Dir}",
            request.Share, request.Path, request.IsDirectory);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyRename(NotifyRenameRequest request, ServerCallContext context)
    {
        var svc = await ResolveServiceAsync(
            request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null) return Fail();

        await svc.NotifyExternalRenameAsync(request.OldPath, request.NewPath, request.IsDirectory);
        _logger.LogInformation("NotifyRename: share={Share} [{Old}] -> [{New}] dir={Dir}",
            request.Share, request.OldPath, request.NewPath, request.IsDirectory);
        return Ok();
    }

    // ---- helpers ----

    private async Task<(IFileService? Service, UserContext? User)> ResolveAsync(
        string username,
        string share,
        CancellationToken cancellationToken)
    {
        var user = await _auth.ResolveUserContextAsync(username);
        var svc = await ResolveServiceAsync(share, cancellationToken);
        return (svc, user);
    }

    private async Task<IFileService?> ResolveServiceAsync(
        string share,
        CancellationToken cancellationToken)
    {
        var def = await _shares.ResolveEnabledShareAsync(
            share, cancellationToken);
        if (def is null)
        {
            _logger.LogWarning(
                "Event for unknown or disabled share '{Share}' discarded.",
                share);
            return null;
        }
        return _factory.CreateForShare(def.Id, def.Path);
    }

    private static NotifyReply Ok() => new() { Ok = true };
    private static NotifyReply Fail() => new() { Ok = false };
}
