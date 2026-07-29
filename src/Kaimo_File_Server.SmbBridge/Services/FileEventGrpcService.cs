using Grpc.Core;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
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
    private const string CloseCaptureDirectory = ".kaimo-close-captures";
    private readonly IFileServiceFactory _factory;
    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly ISambaLifecycleEventRepository _events;
    private readonly ILogger<FileEventGrpcService> _logger;

    public FileEventGrpcService(
        IFileServiceFactory factory,
        IShareRepository shares,
        IAuthenticationLookup auth,
        ISambaLifecycleEventRepository events,
        ILogger<FileEventGrpcService> logger)
    {
        _factory = factory;
        _shares = shares;
        _auth = auth;
        _events = events;
        _logger = logger;
    }

    public override async Task<NotifyReply> NotifyClose(NotifyCloseRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share)) return Fail();
        if (!TryParseEventId(request.EventId, out var eventId)) return Fail();
        if (!IsCaptureId(request.CaptureId)) return Fail();
        if (!TryEventPath(request.Path, out string path)) return Fail();
        var (svc, user) = await ResolveAsync(
            request.Username, request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null || user is null) return Fail();

        var capturePath = svc.ToAbsolutePath(
            $"{CloseCaptureDirectory}/{request.CaptureId}.cap");
        if (!await ProcessOnceAsync(
                eventId, "close",
                () => svc.NotifyExternalCloseAsync(
                    path, user, () => OpenCaptureAsync(capturePath)),
                context?.CancellationToken ?? CancellationToken.None))
            return Fail();
        try
        {
            File.Delete(capturePath);
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Completed close capture {CaptureId} could not be removed.",
                request.CaptureId);
            return Fail();
        }
        _logger.LogInformation("NotifyClose: share={Share} path=[{Path}] user={User}",
            request.Share, request.Path, request.Username);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyMkdir(NotifyPathRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share)) return Fail();
        if (!TryParseEventId(request.EventId, out var eventId)) return Fail();
        if (!TryEventPath(request.Path, out string path)) return Fail();
        var (svc, user) = await ResolveAsync(
            request.Username, request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null || user is null) return Fail();

        if (!await ProcessOnceAsync(
                eventId, "mkdir",
                () => svc.NotifyExternalMkdirAsync(path, user),
                context?.CancellationToken ?? CancellationToken.None))
            return Fail();
        return Ok();
    }

    public override async Task<NotifyReply> NotifyDelete(NotifyPathRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share)) return Fail();
        if (!TryParseEventId(request.EventId, out var eventId)) return Fail();
        if (!TryEventPath(request.Path, out string path)) return Fail();
        var svc = await ResolveServiceAsync(
            request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null) return Fail();

        if (!await ProcessOnceAsync(
                eventId, "delete",
                () => svc.NotifyExternalDeleteAsync(
                    path, request.IsDirectory),
                context?.CancellationToken ?? CancellationToken.None))
            return Fail();
        _logger.LogInformation("NotifyDelete: share={Share} path=[{Path}] dir={Dir}",
            request.Share, request.Path, request.IsDirectory);
        return Ok();
    }

    public override async Task<NotifyReply> NotifyRename(NotifyRenameRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidContext(request.Username, request.Share)) return Fail();
        if (!TryParseEventId(request.EventId, out var eventId)) return Fail();
        if (!TryEventPath(request.OldPath, out string oldPath) ||
            !TryEventPath(request.NewPath, out string newPath))
            return Fail();
        var svc = await ResolveServiceAsync(
            request.Share,
            context?.CancellationToken ?? CancellationToken.None);
        if (svc is null) return Fail();

        if (!await ProcessOnceAsync(
                eventId, "rename",
                () => svc.NotifyExternalRenameAsync(
                    oldPath, newPath, request.IsDirectory,
                    eventId),
                context?.CancellationToken ?? CancellationToken.None))
            return Fail();
        _logger.LogInformation("NotifyRename: share={Share} [{Old}] -> [{New}] dir={Dir}",
            request.Share, request.OldPath, request.NewPath, request.IsDirectory);
        return Ok();
    }

    // ---- helpers ----

    private async Task<bool> ProcessOnceAsync(
        Guid eventId,
        string eventType,
        Func<Task> handler,
        CancellationToken cancellationToken)
    {
        var claim = await _events.TryClaimAsync(
            eventId, eventType, TimeSpan.FromMinutes(2), cancellationToken);
        if (claim == SambaEventClaimResult.AlreadyCompleted) return true;
        if (claim != SambaEventClaimResult.Acquired)
        {
            _logger.LogWarning(
                "Lifecycle event {EventId} ({EventType}) was not claimed: {Claim}.",
                eventId, eventType, claim);
            return false;
        }

        try
        {
            await handler();
            await _events.CompleteAsync(eventId, cancellationToken);
            return true;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Lifecycle event {EventId} ({EventType}) failed and will be retried.",
                eventId, eventType);
            await _events.ReleaseAsync(
                eventId, error.Message, CancellationToken.None);
            return false;
        }
    }

    private static bool TryParseEventId(string value, out Guid eventId) =>
        Guid.TryParseExact(value, "N", out eventId);

    private static bool IsCaptureId(string value) =>
        value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryEventPath(string rawPath, out string normalized) =>
        ShareRelativePath.TryNormalizeStrict(
            rawPath, out normalized, allowRoot: false,
            allowInternalNamespace: false);

    private static Task<Stream> OpenCaptureAsync(string path)
    {
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

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
