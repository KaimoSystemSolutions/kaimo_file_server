using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Exports a share's permission state (share definition, owners and ACL entries of every
/// metadata row) into a JSON file that is stored inside each share snapshot. Principals are
/// written with id and name so a restore can skip principals that no longer exist and
/// report them by name. Streamed, so very large shares never load fully into memory.
/// </summary>
public static class BackupAclManifest
{
    public const string FileName = "kaimo-acl-manifest.json";
    public const int FormatVersion = 1;

    public static async Task WriteAsync(
        ApplicationDbContext db, ShareDefinition share, string path, DateTime exportedAtUtc, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var u in await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username }).ToListAsync(ct))
            names[u.Id] = u.Username;
        foreach (var g in await db.Groups.AsNoTracking().Select(g => new { g.Id, g.Name }).ToListAsync(ct))
            names.TryAdd(g.Id, g.Name);
        foreach (var r in await db.Roles.AsNoTracking().Select(r => new { r.Id, r.Name }).ToListAsync(ct))
            names.TryAdd(r.Id, r.Name);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using var stream = new FileStream(path, options);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("exportedAtUtc", exportedAtUtc);
        writer.WriteStartObject("share");
        writer.WriteString("id", share.Id);
        writer.WriteString("name", share.Name);
        writer.WriteString("path", share.Path);
        writer.WriteString("departmentId", share.DepartmentId);
        writer.WriteBoolean("isUserHomes", share.IsUserHomes);
        writer.WriteBoolean("isRecycleEnabled", share.IsRecycleEnabled);
        writer.WriteEndObject();

        writer.WriteStartArray("entries");
        var rows = db.FileMetadata.AsNoTracking()
            .Where(m => m.ShareId == share.Id)
            .Include(m => m.Acl)
            .OrderBy(m => m.Path)
            .AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(ct))
        {
            writer.WriteStartObject();
            writer.WriteString("path", row.Path);
            writer.WriteBoolean("isDirectory", row.IsDirectory);
            writer.WriteString("ownerId", row.OwnerId);
            writer.WriteString("ownerName", names.GetValueOrDefault(row.OwnerId));
            writer.WriteStartArray("acl");
            foreach (var entry in row.Acl)
            {
                writer.WriteStartObject();
                writer.WriteString("principalId", entry.PrincipalId);
                writer.WriteString("principalName", names.GetValueOrDefault(entry.PrincipalId));
                writer.WriteNumber("entryType", (int)entry.EntryType);
                writer.WriteNumber("permissions", (long)entry.Permissions);
                writer.WriteNumber("inheritance", (int)entry.Inheritance);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            if (writer.BytesPending > 64 * 1024)
                await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }
}
