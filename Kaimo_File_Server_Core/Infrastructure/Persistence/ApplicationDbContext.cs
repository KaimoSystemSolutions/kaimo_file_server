using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server_Core.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<UserGroup> UserGroups { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }
        public DbSet<FileMetadata> FileMetadata { get; set; }
        public DbSet<AccessEntry> AccessEntries { get; set; }
        public DbSet<ShareAccessEntry> ShareAccessEntries { get; set; }
        public DbSet<ShareDefinition> ShareDefinitions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            
            modelBuilder.Entity<Identity>().UseTpcMappingStrategy();
            modelBuilder.Entity<Identity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            });

            // Users
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.Property(e => e.NtHash).IsRequired().HasMaxLength(64);
            });

            // Groups
            modelBuilder.Entity<Group>(entity =>
            {
                entity.ToTable("groups");
            });

            // Roles
            modelBuilder.Entity<Role>(entity =>
            {
                entity.ToTable("roles");
            });

            // UserGroup
            modelBuilder.Entity<UserGroup>(entity =>
            {
                entity.ToTable("user_groups");
                entity.HasKey(e => new { e.UserId, e.GroupId });
            });

            // UserRole
            modelBuilder.Entity<UserRole>(entity =>
            {
                entity.ToTable("user_roles");
                entity.HasKey(e => new { e.UserId, e.RoleId });
            });

            // FileMetadata
            modelBuilder.Entity<FileMetadata>(entity =>
            {
                entity.ToTable("file_metadata");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Path).IsRequired();
                entity.HasIndex(e => e.Path).IsUnique();
                entity.HasMany(e => e.Acl)
                      .WithOne()
                      .HasForeignKey(e => e.FileMetadataId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // AccessEntry
            modelBuilder.Entity<AccessEntry>(entity =>
            {
                entity.ToTable("access_entries");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Permissions).HasConversion<long>();
                entity.Property(e => e.Inheritance).HasConversion<int>();
                entity.Property(e => e.EntryType).HasConversion<int>();
            });

            // ShareAccesEntry
            modelBuilder.Entity<ShareAccessEntry>(entity =>
            {
                entity.ToTable("share_access");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ShareName).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => new { e.ShareName, e.PrincipalId }).IsUnique();
            });

            // ShareRepo
            modelBuilder.Entity<ShareDefinition>(entity =>
            {
                entity.ToTable("share_definitions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.Property(e => e.Path).IsRequired();
            });
        }
    }
}
