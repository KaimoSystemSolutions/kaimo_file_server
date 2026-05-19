using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<UserGroup> UserGroups { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }
        public DbSet<FileMetadata> FileMetadata { get; set; }
        public DbSet<AccessEntry> AccessEntries { get; set; }
        public DbSet<ShareAccessEntry> ShareAccessEntries { get; set; }
        public DbSet<ShareDefinition> ShareDefinitions { get; set; }
        public DbSet<FileVersion> FileVersions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Identity>().UseTpcMappingStrategy();
            modelBuilder.Entity<Identity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            });

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.Property(e => e.NtHash).IsRequired().HasMaxLength(64);
            });

            modelBuilder.Entity<Group>(entity => { entity.ToTable("groups"); });
            modelBuilder.Entity<Role>(entity => { entity.ToTable("roles"); });

            modelBuilder.Entity<UserGroup>(entity =>
            {
                entity.ToTable("user_groups");
                entity.HasKey(e => new { e.UserId, e.GroupId });
            });

            modelBuilder.Entity<UserRole>(entity =>
            {
                entity.ToTable("user_roles");
                entity.HasKey(e => new { e.UserId, e.RoleId });
            });

            // In OnModelCreating, den FileMetadata-Block ersetzen:
            modelBuilder.Entity<FileMetadata>(entity =>
            {
                entity.ToTable("file_metadata");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ShareId).IsRequired();
                entity.Property(e => e.Path).IsRequired();
                entity.Property(e => e.OwnerId).IsRequired();

                // Unique pro Share + Pfad
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

            modelBuilder.Entity<ShareAccessEntry>(entity =>
            {
                entity.ToTable("share_access");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ShareName).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => new { e.ShareName, e.PrincipalId }).IsUnique();
            });

            modelBuilder.Entity<ShareDefinition>(entity =>
            {
                entity.ToTable("share_definitions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.Property(e => e.Path).IsRequired();
            });

            modelBuilder.Entity<FileVersion>(entity =>
            {
                entity.ToTable("file_versions");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.FilePath)
                    .IsRequired()
                    .HasMaxLength(1000);

                entity.Property(e => e.SnapshotTimestampUtc)
                    .IsRequired();

                entity.Property(e => e.StoragePath)
                    .IsRequired()
                    .HasMaxLength(500);

                entity.Property(e => e.ContentHash)
                    .IsRequired()
                    .HasMaxLength(64); // SHA-256 hex = 64 chars

                entity.Property(e => e.CreatedBy)
                    .HasMaxLength(200);

                // Unique: one file can't have two versions at the exact same second
                entity.HasIndex(e => new { e.FilePath, e.SnapshotTimestampUtc })
                    .IsUnique();

                // Fast lookup for "all snapshots" query
                entity.HasIndex(e => e.SnapshotTimestampUtc);

                // Fast lookup for "latest version hash" dedup check
                entity.HasIndex(e => new { e.FilePath, e.ContentHash });
            });
        }
    }
}
