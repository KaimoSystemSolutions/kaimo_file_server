using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>File metadata, ACL entries, shares, recycle bin, versions and Samba lifecycle receipts.</summary>
    private void ConfigureFilesAndShares(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileMetadata>(entity =>
        {
            entity.ToTable("file_metadata");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ShareId).IsRequired();
            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.OwnerId).IsRequired();
            entity.Property(e => e.LastAccessedAt);

            entity.HasIndex(e => new { e.ShareId, e.Path }).IsUnique();

            entity.HasMany(e => e.Acl)
                  .WithOne()
                  .HasForeignKey(e => e.FileMetadataId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessEntry>(entity =>
        {
            entity.ToTable("access_entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Permissions).HasConversion<long>();
            entity.Property(e => e.Inheritance).HasConversion<int>();
            entity.Property(e => e.EntryType).HasConversion<int>();
        });

        modelBuilder.Entity<ShareDefinition>(entity =>
        {
            entity.ToTable("share_definitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.IsEnabled).IsRequired();
            entity.Property(e => e.IsShareHidden).IsRequired();
            entity.Property(e => e.IsRecycleEnabled).IsRequired();
            entity.Property(e => e.IsUserHomes).IsRequired().HasDefaultValue(false);
            // At most one home-folder share may exist.
            entity.HasIndex(e => e.IsUserHomes).IsUnique().HasFilter("\"IsUserHomes\"");
            entity.Property(e => e.RecycleMaxSizeMode).HasConversion<int>().IsRequired();

            entity.Property(e => e.CloudSettings)
                .HasConversion(
                    v => v.Serialize(),
                    v => CloudSettings.Deserialize(v))
                .HasColumnType("text");
            
            entity.Ignore(e => e.CloudConnection);

            // Direct FK to Department (required, default = Global)
            entity.Property(e => e.DepartmentId)
                .IsRequired()
                .HasDefaultValue(WellKnownGUIDs.DEPARTMENT_GLOBAL);

            entity.HasIndex(e => e.DepartmentId);
        });

        // Recycle-bin janitor bookkeeping (see RecycleBinJanitor); rebuildable from disk.
        modelBuilder.Entity<RecycleBinEntry>(entity =>
        {
            entity.ToTable("recycle_bin_entries");
            entity.HasKey(e => new { e.ShareId, e.Path });
            entity.HasIndex(e => new { e.ShareId, e.FirstSeenUtc });
            entity.HasOne<ShareDefinition>().WithMany().HasForeignKey(e => e.ShareId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileVersion>(entity =>
        {
            entity.ToTable("file_versions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ShareId).IsRequired();
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.SnapshotTimestampUtc).IsRequired();
            entity.Property(e => e.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedBy).HasMaxLength(200);
            // Version history is scoped per share: the same share-relative path
            // may exist in multiple shares, so every uniqueness/lookup index is
            // keyed by ShareId first.
            entity.HasIndex(e => new { e.ShareId, e.FilePath, e.SnapshotTimestampUtc }).IsUnique();
            entity.HasIndex(e => e.SnapshotTimestampUtc);
            entity.HasIndex(e => new { e.ShareId, e.FilePath, e.ContentHash });
        });

        modelBuilder.Entity<SambaLifecycleEventReceipt>(entity =>
        {
            entity.ToTable("samba_lifecycle_event_receipts");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(32);
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.AttemptCount).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.HasIndex(e => e.CompletedAtUtc);
            entity.HasIndex(e => e.LeaseUntilUtc);
        });
    }
}
