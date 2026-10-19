using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Client API (mobile/desktop apps): devices, tokens, sync profiles, request receipts and the file change log.</summary>
    private void ConfigureClientApi(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncDevice>(entity =>
        {
            entity.ToTable("sync_devices");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Platform).HasMaxLength(50);
            entity.Property(e => e.PushToken).HasMaxLength(500);
            entity.Property(e => e.HardwareIdHash).HasMaxLength(64);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.HardwareIdHash });
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.DeviceId).IsRequired();
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.UserId);
            // UserId is denormalized (no FK) to avoid a second cascade path
            // into refresh_tokens; deletes cascade through the device.
            entity.HasOne<SyncDevice>().WithMany().HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceSyncProfile>(entity =>
        {
            entity.ToTable("device_sync_profiles");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.DeviceId).IsRequired();
            entity.Property(e => e.ShareId).IsRequired();
            entity.Property(e => e.RelativePath).IsRequired().HasMaxLength(2000);
            // Opaque device-local path; the server stores it verbatim.
            entity.Property(e => e.LocalPath).IsRequired().HasMaxLength(4000).HasDefaultValue(string.Empty);
            entity.Property(e => e.Mode).HasConversion<int>();
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.DeviceId, e.ShareId, e.RelativePath }).IsUnique();
            entity.HasOne<SyncDevice>().WithMany().HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            // ShareId has no FK cascade here: shares are removed through their
            // own admin flow, and a dangling profile is harmless (the API
            // resolves the share and returns not-found).
            entity.HasIndex(e => e.ShareId);
        });

        modelBuilder.Entity<ClientRequestReceipt>(entity =>
        {
            entity.ToTable("client_request_receipts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.RequestHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.StatusCode).IsRequired();
            // One outcome per (device, key): a retry finds it and replays it.
            entity.HasIndex(e => new { e.DeviceId, e.IdempotencyKey }).IsUnique();
            // Drives retention pruning of old receipts.
            entity.HasIndex(e => e.CreatedAtUtc);
            entity.HasOne<SyncDevice>().WithMany().HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileChangeLogEntry>(entity =>
        {
            entity.ToTable("file_change_log");
            entity.HasKey(e => e.Seq);
            // Database-assigned monotonic identity (Npgsql IDENTITY; SQLite AUTOINCREMENT).
            entity.Property(e => e.Seq).ValueGeneratedOnAdd();
            entity.Property(e => e.ShareId).IsRequired();
            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.ChangeType).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            // The client's "since N under this subtree" cursor scan and the head-seq lookup.
            entity.HasIndex(e => new { e.ShareId, e.Seq });
            // Drives retention pruning of old entries.
            entity.HasIndex(e => e.CreatedAtUtc);
        });
    }
}
