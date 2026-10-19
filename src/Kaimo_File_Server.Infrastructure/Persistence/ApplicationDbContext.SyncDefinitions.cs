using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Sync definitions and their runtime state.</summary>
    private void ConfigureSyncDefinitions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncDefinition>(entity =>
        {
            entity.ToTable("sync_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LocalPath).IsRequired().HasMaxLength(2000);
            entity.Property(x => x.RemotePath).IsRequired().HasMaxLength(2000);
            entity.Property(x => x.RemoteProviderItemId).HasMaxLength(500);
            entity.Property(x => x.Mode).HasConversion<int>();
            entity.Property(x => x.Schedule)
                .HasConversion(
                    value => SyncDefinition.SerializeSchedule(value),
                    value => SyncDefinition.DeserializeSchedule(value))
                .HasColumnType("text");
            entity.Property(x => x.AdvancedSettings)
                .HasConversion(
                    value => SyncDefinition.SerializeAdvancedSettings(value),
                    value => SyncDefinition.DeserializeAdvancedSettings(value))
                .HasColumnType("text");
            entity.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Description).IsRequired().HasMaxLength(2000);
            entity.Property(x => x.MigrationSource).HasMaxLength(100);
            entity.Property(x => x.MigrationSourceChecksum).HasMaxLength(64);
            entity.HasIndex(x => new { x.LocalShareId, x.LocalPath }).IsUnique();
            entity.HasIndex(x => x.ConnectionId);
            entity.HasIndex(x => new { x.Enabled, x.RunAsUserId });
            entity.HasOne<StorageConnection>().WithMany().HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShareDefinition>().WithMany().HasForeignKey(x => x.LocalShareId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.RunAsUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncDefinitionRuntime>(entity =>
        {
            entity.ToTable("sync_definition_runtimes");
            entity.HasKey(x => x.SyncDefinitionId);
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.LastErrorCode).HasMaxLength(200);
            entity.Property(x => x.LastSyncManifest).HasColumnType("text");
            entity.Property(x => x.LastRunFailures).HasColumnType("text");
            entity.HasIndex(x => x.LastSuccessfulRunAtUtc);
            entity.HasOne<SyncDefinition>().WithOne()
                .HasForeignKey<SyncDefinitionRuntime>(x => x.SyncDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
