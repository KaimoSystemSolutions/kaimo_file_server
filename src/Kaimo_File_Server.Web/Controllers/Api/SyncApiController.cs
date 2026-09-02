using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// The sync surface: per-device folder selections, delta enumeration, and a
/// battery-friendly long-poll change notification. All server state here is
/// selection intent — the sync loop itself runs on the client.
/// </summary>
[Authorize]
[Route("api/v1/sync")]
public sealed class SyncApiController : ApiControllerBase
{
    // How long a single change-wait request blocks before returning "no change".
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    // How often the wait loop samples the change cursor.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IUserContextFactory _userContextFactory;
    private readonly ISyncDeviceRepository _devices;
    private readonly IDeviceSyncProfileRepository _profiles;
    private readonly IShareRepository _shares;
    private readonly IFileServiceFactory _fileServiceFactory;
    private readonly ISyncQueryService _syncQuery;
    private readonly IFileChangeCursorRepository _changeCursors;
    private readonly IFileChangeLogRepository _changeLog;

    // Default and ceiling for one change-feed page.
    private const int DefaultChangeLimit = 1000;
    private const int MaxChangeLimit = 5000;

    public SyncApiController(
        IUserContextFactory userContextFactory,
        ISyncDeviceRepository devices,
        IDeviceSyncProfileRepository profiles,
        IShareRepository shares,
        IFileServiceFactory fileServiceFactory,
        ISyncQueryService syncQuery,
        IFileChangeCursorRepository changeCursors,
        IFileChangeLogRepository changeLog)
    {
        _userContextFactory = userContextFactory;
        _devices = devices;
        _profiles = profiles;
        _shares = shares;
        _fileServiceFactory = fileServiceFactory;
        _syncQuery = syncQuery;
        _changeCursors = changeCursors;
        _changeLog = changeLog;
    }

    // ─────────────────────────── devices ───────────────────────────

    /// <summary>Lists the caller's registered devices.</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var devices = await _devices.GetByUserAsync(user.User.Id);
        return Ok(devices.Select(DeviceDto.From).ToList());
    }

    // ─────────────────────────── profiles ───────────────────────────

    /// <summary>Lists sync selections — all the caller's, or a single device's.</summary>
    [HttpGet("profiles")]
    public async Task<IActionResult> GetProfiles([FromQuery] Guid? deviceId)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        List<DeviceSyncProfile> profiles;
        if (deviceId is { } id)
        {
            var device = await _devices.GetByIdAsync(id);
            if (device is null || device.UserId != user.User.Id) return ApiNotFound("Device not found.");
            profiles = await _profiles.GetByDeviceAsync(id);
        }
        else
        {
            profiles = await _profiles.GetByUserAsync(user.User.Id);
        }

        return Ok(profiles.Select(SyncProfileDto.From).ToList());
    }

    /// <summary>Creates a sync selection for one of the caller's devices.</summary>
    [HttpPost("profiles")]
    public async Task<IActionResult> CreateProfile([FromBody] CreateSyncProfileRequest request)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();
        if (request is null) return ApiBadRequest("invalid_request", "Body is required.");

        var device = await _devices.GetByIdAsync(request.DeviceId);
        if (device is null || device.UserId != user.User.Id) return ApiNotFound("Device not found.");

        var share = await _shares.GetByIdAsync(request.ShareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        if (!ShareRelativePath.TryNormalizeStrict(request.RelativePath ?? string.Empty, out var path))
            return ApiBadRequest("invalid_path", "RelativePath is not a valid share-relative path.");

        // The user must be able to list the chosen subtree to sync it.
        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        if (!await fs.CanListAsync(path, user)) return ApiForbidden();

        var now = DateTime.UtcNow;
        var created = await _profiles.CreateAsync(new DeviceSyncProfile
        {
            DeviceId = device.Id,
            UserId = user.User.Id,
            ShareId = share.Id,
            RelativePath = path,
            LocalPath = request.LocalPath?.Trim() ?? string.Empty,
            Mode = request.Mode,
            Enabled = request.Enabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        return CreatedAtAction(nameof(GetProfiles), new { deviceId = device.Id },
            SyncProfileDto.From(created));
    }

    /// <summary>Updates an existing sync selection owned by the caller.</summary>
    [HttpPut("profiles/{id:guid}")]
    public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateSyncProfileRequest request)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();
        if (request is null) return ApiBadRequest("invalid_request", "Body is required.");

        var profile = await _profiles.GetByIdAsync(id);
        if (profile is null || profile.UserId != user.User.Id) return ApiNotFound("Profile not found.");

        if (!ShareRelativePath.TryNormalizeStrict(request.RelativePath ?? string.Empty, out var path))
            return ApiBadRequest("invalid_path", "RelativePath is not a valid share-relative path.");

        var share = await _shares.GetByIdAsync(profile.ShareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        if (!await fs.CanListAsync(path, user)) return ApiForbidden();

        profile.RelativePath = path;
        profile.LocalPath = request.LocalPath?.Trim() ?? string.Empty;
        profile.Mode = request.Mode;
        profile.Enabled = request.Enabled;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _profiles.UpdateAsync(profile);

        return Ok(SyncProfileDto.From(profile));
    }

    /// <summary>Deletes a sync selection owned by the caller.</summary>
    [HttpDelete("profiles/{id:guid}")]
    public async Task<IActionResult> DeleteProfile(Guid id)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var profile = await _profiles.GetByIdAsync(id);
        if (profile is null || profile.UserId != user.User.Id) return ApiNotFound("Profile not found.");

        await _profiles.DeleteAsync(id);
        return NoContent();
    }

    // ─────────────────────────── delta & change wait ───────────────────────────

    /// <summary>Enumerates a synced subtree so the client can compute its delta.</summary>
    [HttpGet("{shareId:guid}/delta")]
    public async Task<IActionResult> Delta(Guid shareId, [FromQuery] string? path)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var share = await _shares.GetByIdAsync(shareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        if (!ShareRelativePath.TryNormalizeStrict(path ?? string.Empty, out var root))
            return ApiBadRequest("invalid_path", "The path is not a valid share-relative path.");

        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        if (!await fs.CanListAsync(root, user)) return ApiForbidden();

        var delta = await _syncQuery.EnumerateAsync(shareId, root, user, HttpContext.RequestAborted);
        var dto = new SyncDeltaDto(
            delta.Entries.Select(SyncEntryDto.From).ToList(), delta.Token, delta.Seq);
        return Ok(dto);
    }

    /// <summary>
    /// Incremental change feed: returns the change-log entries for a subtree with a sequence greater
    /// than <paramref name="since"/>, so a client fetches only the true deltas — renames and
    /// net-zero changes included — instead of re-enumerating the whole subtree. Bootstrap the cursor
    /// from a full <c>delta</c> (its <c>seq</c>), then advance it by the returned <c>seq</c>.
    /// </summary>
    [HttpGet("{shareId:guid}/changes")]
    public async Task<IActionResult> Changes(
        Guid shareId, [FromQuery] long since, [FromQuery] string? path, [FromQuery] int? limit)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var share = await _shares.GetByIdAsync(shareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        if (!ShareRelativePath.TryNormalizeStrict(path ?? string.Empty, out var root))
            return ApiBadRequest("invalid_path", "The path is not a valid share-relative path.");

        // Same gate as delta/wait: list access on the watched subtree, so the feed cannot be used to
        // probe for hidden content.
        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        if (!await fs.CanListAsync(root, user)) return ApiForbidden();

        var ct = HttpContext.RequestAborted;
        int pageSize = Math.Clamp(limit ?? DefaultChangeLimit, 1, MaxChangeLimit);

        // Fetch one extra to detect truncation without a second query.
        var raw = await _changeLog.GetChangesSinceAsync(shareId, since, root, pageSize + 1, ct);
        bool truncated = raw.Count > pageSize;
        var page = truncated ? raw.Take(pageSize).ToList() : raw;

        // ACL-filter live entries the same way ListAsync does — per-file ACLs can differ inside a
        // listable folder. Deleted / renamed-away entries (their ACL is gone) stay in, because they
        // fall under the caller-authorized root; this mirrors how delta only ever walks what the
        // caller can list.
        var visible = await FilterVisibleAsync(fs, user, page);

        // Advance the cursor even when the page is empty or fully filtered, so the client never
        // re-requests the same range: last raw seq on this page, else the subtree head.
        long nextSeq = page.Count > 0
            ? page[^1].Seq
            : await _changeLog.GetHeadSeqAsync(shareId, root, ct);

        // Detect a cursor that predates the retained log: retention pruning drops a low-seq prefix,
        // so a client offline longer than the window would silently miss the pruned mutations. Tell
        // it to re-bootstrap via delta instead of trusting an incomplete incremental page.
        bool reset = IsCursorStale(since, await _changeLog.GetOldestSeqAsync(ct));

        var dto = new ChangesFeedDto(
            visible.Select(FileChangeDto.From).ToList(), nextSeq, truncated, reset);
        return Ok(dto);
    }

    /// <summary>
    /// Whether an incremental cursor has a retention gap: the client last saw <paramref name="since"/>
    /// and wants everything after it, but the oldest entry still retained is <paramref name="oldestSeq"/>.
    /// Safe only when the next needed sequence (<c>since + 1</c>) is still present, i.e.
    /// <c>since + 1 &gt;= oldestSeq</c>. An empty log (<paramref name="oldestSeq"/> == 0) never resets.
    /// </summary>
    public static bool IsCursorStale(long since, long oldestSeq)
        => oldestSeq > 0 && since + 1 < oldestSeq;

    /// <summary>
    /// Drops change entries whose live target the caller may not list. Entries whose item no longer
    /// exists (<see cref="FileChangeType.Deleted"/>, and the source of a <see cref="FileChangeType.Renamed"/>)
    /// are kept — their ACL is gone, and they already fall under the caller-authorized subtree root.
    /// </summary>
    private static async Task<List<FileChangeLogEntry>> FilterVisibleAsync(
        IFileService fs, UserContext user, IReadOnlyList<FileChangeLogEntry> entries)
    {
        // The current-existence paths that carry a live ACL to check.
        var liveItems = entries
            .Where(e => e.ChangeType is FileChangeType.Created
                     or FileChangeType.Modified
                     or FileChangeType.Renamed
                     or FileChangeType.SubtreeChanged)
            .Select(e => (e.Path, e.IsDirectory))
            .ToList();

        var readable = liveItems.Count > 0
            ? await fs.FilterReadablePathsAsync(liveItems, user)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new List<FileChangeLogEntry>(entries.Count);
        foreach (var e in entries)
        {
            bool keep = e.ChangeType switch
            {
                FileChangeType.Deleted => true,
                FileChangeType.Created or FileChangeType.Modified
                    or FileChangeType.Renamed or FileChangeType.SubtreeChanged
                    => readable.Contains(e.Path),
                _ => true,
            };
            if (keep) result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// Long-polls for a change to a share subtree. Returns as soon as the change
    /// token differs from <paramref name="since"/>, or after a bounded timeout
    /// with <c>changed=false</c> — one idle request instead of a busy poll loop.
    /// </summary>
    [HttpGet("changes/wait")]
    public async Task<IActionResult> WaitForChange(
        [FromQuery] Guid shareId, [FromQuery] string? path, [FromQuery] string? since)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        var share = await _shares.GetByIdAsync(shareId);
        if (share is null || !share.IsEnabled) return ApiNotFound("Share not found.");

        if (!ShareRelativePath.TryNormalizeStrict(path ?? string.Empty, out var root))
            return ApiBadRequest("invalid_path", "The path is not a valid share-relative path.");

        // Only let a caller wait on a subtree they may list, so this cannot be
        // used to probe for the existence of hidden content.
        var fs = _fileServiceFactory.CreateForShare(share.Id, share.Path);
        if (!await fs.CanListAsync(root, user)) return ApiForbidden();

        Response.Headers[HeaderNames.CacheControl] = "no-store";

        var deadline = DateTime.UtcNow + WaitTimeout;
        var ct = HttpContext.RequestAborted;

        while (true)
        {
            // The change-log head is the token: unlike the coarse file_metadata fingerprint it
            // advances on renames and content overwrites too, so this wakes on every mutation.
            var current = (await _changeLog.GetHeadSeqAsync(shareId, root, ct))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(since) || current != since)
                return Ok(new ChangeWaitDto(current, Changed: current != since));

            if (DateTime.UtcNow >= deadline)
                return Ok(new ChangeWaitDto(current, Changed: false));

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (TaskCanceledException)
            {
                // Client disconnected mid-wait; nothing to return.
                return new EmptyResult();
            }
        }
    }
}
