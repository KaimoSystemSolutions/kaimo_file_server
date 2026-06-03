using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        // ── Existing ──
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

        // ── Departments & Scoped Roles ──
        public DbSet<Department> Departments { get; set; }
        public DbSet<DepartmentUser> DepartmentUsers { get; set; }
        public DbSet<DepartmentGroup> DepartmentGroups { get; set; }
        public DbSet<DepartmentShare> DepartmentShares { get; set; }
        public DbSet<ScopedRoleAssignment> ScopedRoleAssignments { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ── Existing configurations (unchanged) ──

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
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.Email).HasMaxLength(254);
                entity.Property(e => e.IsEnabled).IsRequired().HasDefaultValue(true);
                entity.Property(e => e.CanChangePassword).IsRequired().HasDefaultValue(true);
            });

            modelBuilder.Entity<Group>(entity => { entity.ToTable("groups"); });

            modelBuilder.Entity<Role>(entity =>
            {
                entity.ToTable("roles");
                entity.Property(e => e.ManagementPermissions)
                    .HasConversion<long>()
                    .HasDefaultValue(ManagementPermission.None);
                entity.Property(e => e.IsSystemRole)
                    .IsRequired()
                    .HasDefaultValue(false);
            });

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
                entity.Property(e => e.IsEnabled).IsRequired();
                entity.Property(e => e.IsRecycleEnabled).IsRequired();
            });

            modelBuilder.Entity<FileVersion>(entity =>
            {
                entity.ToTable("file_versions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000);
                entity.Property(e => e.SnapshotTimestampUtc).IsRequired();
                entity.Property(e => e.StoragePath).IsRequired().HasMaxLength(500);
                entity.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
                entity.Property(e => e.CreatedBy).HasMaxLength(200);
                entity.HasIndex(e => new { e.FilePath, e.SnapshotTimestampUtc }).IsUnique();
                entity.HasIndex(e => e.SnapshotTimestampUtc);
                entity.HasIndex(e => new { e.FilePath, e.ContentHash });
            });

            // ══════════════════════════════════════════════════
            // Department & Scoped Role Assignment tables
            // ══════════════════════════════════════════════════

            modelBuilder.Entity<Department>(entity =>
            {
                entity.ToTable("departments");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.ParentDepartmentId);

                // NEW: Default file permission for department members on department shares.
                // Nullable — null means "inherit from parent department".
                // Stored as long? mapping to FilePermission flags.
                entity.Property(e => e.DefaultFilePermission)
                    .HasColumnName("default_file_permission")
                    .IsRequired(false);

                // Index for hierarchy traversal
                entity.HasIndex(e => e.ParentDepartmentId);
            });

            modelBuilder.Entity<DepartmentUser>(entity =>
            {
                entity.ToTable("department_users");
                entity.HasKey(e => new { e.DepartmentId, e.UserId });

                // Index for "which departments does user X belong to?"
                entity.HasIndex(e => e.UserId);
            });

            modelBuilder.Entity<DepartmentGroup>(entity =>
            {
                entity.ToTable("department_groups");
                entity.HasKey(e => new { e.DepartmentId, e.GroupId });

                entity.HasIndex(e => e.GroupId);
            });

            modelBuilder.Entity<DepartmentShare>(entity =>
            {
                entity.ToTable("department_shares");
                entity.HasKey(e => new { e.DepartmentId, e.ShareId });

                entity.HasIndex(e => e.ShareId);
            });

            modelBuilder.Entity<ScopedRoleAssignment>(entity =>
            {
                entity.ToTable("scoped_role_assignments");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ScopeType).HasConversion<int>();

                entity.HasIndex(e => new { e.PrincipalId, e.RoleId, e.ScopeType, e.ScopeId })
                    .IsUnique();

                entity.HasIndex(e => e.PrincipalId);
                entity.HasIndex(e => new { e.ScopeType, e.ScopeId });

                // NEW: Index for "all assignments for this role"
                entity.HasIndex(e => e.RoleId);
            });
        }
    }
}