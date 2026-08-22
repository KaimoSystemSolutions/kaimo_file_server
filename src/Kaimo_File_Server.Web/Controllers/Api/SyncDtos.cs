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

/// <summary>A subtree enumeration plus its change token.</summary>
public sealed record SyncDeltaDto(IReadOnlyList<SyncEntryDto> Entries, string Token);

/// <summary>Result of a long-poll change wait.</summary>
public sealed record ChangeWaitDto(string Token, bool Changed);
