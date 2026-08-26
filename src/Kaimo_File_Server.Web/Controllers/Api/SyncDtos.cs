using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.Sync;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>A registered device belonging to the caller.</summary>
public sealed record DeviceDto(
    Guid Id, string DisplayName, string Platform,
    DateTime CreatedAtUtc, DateTime LastSeenUtc, bool IsActive)
{
    public static DeviceDto From(SyncDevice d) =>
        new(d.Id, d.DisplayName, d.Platform, d.CreatedAtUtc, d.LastSeenUtc, d.IsActive);
}

/// <summary>
/// A per-device folder sync connection. <see cref="RelativePath"/> is the remote
/// endpoint (a share subtree); <see cref="LocalPath"/> is the device-local
/// endpoint, opaque to the server.
/// </summary>
public sealed record SyncProfileDto(
    Guid Id, Guid DeviceId, Guid ShareId, string RelativePath, string LocalPath,
    SyncMode Mode, bool Enabled, DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
{
    public static SyncProfileDto From(DeviceSyncProfile p) => new(
        p.Id, p.DeviceId, p.ShareId, p.RelativePath, p.LocalPath, p.Mode, p.Enabled,
        p.CreatedAtUtc, p.UpdatedAtUtc);
}

/// <summary>
/// Creates a sync connection for one of the caller's devices. Sync connections are
/// created only by client apps — the web UI never calls this.
/// </summary>
public sealed record CreateSyncProfileRequest(
    Guid DeviceId, Guid ShareId, string? RelativePath, string? LocalPath, SyncMode Mode, bool Enabled = true);

/// <summary>Updates an existing sync connection (client-only).</summary>
public sealed record UpdateSyncProfileRequest(
    string? RelativePath, string? LocalPath, SyncMode Mode, bool Enabled);

/// <summary>One entry in a delta enumeration.</summary>
public sealed record SyncEntryDto(string Path, bool IsDirectory, long Size, DateTime ModifiedAtUtc)
{
    public static SyncEntryDto From(SyncEntry e) =>
        new(e.Path, e.IsDirectory, e.Size, e.ModifiedAtUtc);
}

/// <summary>
/// A subtree enumeration plus its change token and change-log head sequence. Use <see cref="Seq"/>
/// as the baseline cursor for the incremental <c>changes?since=</c> feed.
/// </summary>
public sealed record SyncDeltaDto(IReadOnlyList<SyncEntryDto> Entries, string Token, long Seq);

/// <summary>Result of a long-poll change wait.</summary>
public sealed record ChangeWaitDto(string Token, bool Changed);

/// <summary>
/// One entry in the incremental change feed. <see cref="ChangeType"/> is a
/// <see cref="FileChangeType"/> name (e.g. <c>"Renamed"</c>). <see cref="OldPath"/> is set only for
/// renames (the source). <see cref="Size"/> / <see cref="ModifiedAtUtc"/> are present for
/// creates/modifies so a client can rebuild the item tag without a metadata round trip.
/// </summary>
public sealed record FileChangeDto(
    long Seq, string Path, string? OldPath, string ChangeType,
    bool IsDirectory, long? Size, DateTime? ModifiedAtUtc)
{
    public static FileChangeDto From(FileChangeLogEntry e) => new(
        e.Seq, e.Path, e.OldPath, e.ChangeType.ToString(), e.IsDirectory, e.Size, e.ModifiedAtUtc);
}

/// <summary>
/// A page of the incremental change feed. <see cref="Seq"/> is the cursor the client stores next —
/// the highest sequence in <see cref="Changes"/>, or the subtree head when the page is empty (or was
/// fully filtered by ACLs), so the cursor always advances. <see cref="Truncated"/> is <c>true</c>
/// when more entries remain past the requested limit — call again immediately with the new
/// <see cref="Seq"/>.
/// </summary>
public sealed record ChangesFeedDto(IReadOnlyList<FileChangeDto> Changes, long Seq, bool Truncated);
