using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>File backup (restic): repositories, jobs, runs and snapshots.</summary>
    private void ConfigureBackup(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupRepository>(entity =>
        {
            entity.ToTable("backup_repositories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Backend).HasConversion<int>();
            entity.Property(e => e.State).HasConversion<int>();
            entity.Property(e => e.SettingsJson).IsRequired().HasColumnType("text");
            entity.Property(e => e.EncryptedSecrets).HasColumnType("text");
            entity.Property(e => e.EncryptedPassword).HasColumnType("text");
            entity.Property(e => e.ResticRepositoryId).HasMaxLength(128);
            entity.Property(e => e.LastErrorCode).HasMaxLength(200);
            entity.Property(e => e.MaintenanceSchedule)
                .HasConversion(
                    value => BackupSchedule.Serialize(value),
                    value => BackupSchedule.Deserialize(value))
                .HasColumnType("text");
            // A mirror target that is removed simply stops being mirrored to.
            entity.HasOne<BackupRepository>().WithMany().HasForeignKey(e => e.MirrorTargetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BackupRepositoryDepartment>(entity =>
        {
            entity.ToTable("backup_repository_departments");
            entity.HasKey(e => new { e.RepositoryId, e.DepartmentId });
            entity.HasIndex(e => e.DepartmentId);
            entity.HasOne<BackupRepository>().WithMany().HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Department>().WithMany().HasForeignKey(e => e.DepartmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupJob>(entity =>
        {
            entity.ToTable("backup_jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.ContentLevel).HasConversion<int>();
            entity.Property(e => e.LastStatus).HasConversion<int?>();
            entity.Property(e => e.Schedule)
                .HasConversion(
                    value => BackupSchedule.Serialize(value),
                    value => BackupSchedule.Deserialize(value))
                .HasColumnType("text");
            entity.Property(e => e.Retention)
                .HasConversion(
                    value => BackupRetention.Serialize(value),
                    value => BackupRetention.Deserialize(value))
                .HasColumnType("text");
            entity.HasIndex(e => e.RepositoryId);
            // A repository with jobs cannot be deleted; the jobs must go first.
            entity.HasOne<BackupRepository>().WithMany().HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.Sources).WithOne().HasForeignKey(s => s.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupJobSource>(entity =>
        {
            entity.ToTable("backup_job_sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.Property(e => e.PoolPath).HasMaxLength(2000);
            entity.HasIndex(e => e.JobId);
            entity.HasIndex(e => e.ShareId);
            entity.HasOne<ShareDefinition>().WithMany().HasForeignKey(e => e.ShareId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupRun>(entity =>
        {
            entity.ToTable("backup_runs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Trigger).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.SnapshotIdsJson).HasColumnType("text");
            entity.Property(e => e.ErrorCode).HasMaxLength(200);
            entity.Property(e => e.LogExcerpt).HasColumnType("text");
            entity.Property(e => e.DetailsJson).HasColumnType("text");
            entity.HasIndex(e => e.QueuedAtUtc);
            entity.HasIndex(e => new { e.JobId, e.QueuedAtUtc });
            entity.HasIndex(e => e.Status);
            // Runs are the audit trail: they outlive deleted jobs (JobId stays as a dangling
            // reference) but go with their repository.
            entity.HasOne<BackupRepository>().WithMany().HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupSnapshot>(entity =>
        {
            entity.ToTable("backup_snapshots");
            entity.HasKey(e => new { e.RepositoryId, e.SnapshotId });
            entity.Property(e => e.SnapshotId).HasMaxLength(64);
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.Property(e => e.ContentLevel).HasConversion<int>();
            entity.Property(e => e.PoolPath).HasMaxLength(2000);
            entity.Property(e => e.PathsJson).IsRequired().HasColumnType("text");
            entity.Property(e => e.TagsJson).IsRequired().HasColumnType("text");
            entity.HasIndex(e => new { e.ShareId, e.TimeUtc });
            entity.HasIndex(e => e.JobId);
            entity.HasOne<BackupRepository>().WithMany().HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
