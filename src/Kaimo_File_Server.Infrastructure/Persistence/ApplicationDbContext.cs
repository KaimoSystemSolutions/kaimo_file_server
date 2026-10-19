using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence
{
    public partial class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        // -- Identity --
        public DbSet<User> Users { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<UserGroup> UserGroups { get; set; }
        public DbSet<RevokedWebToken> RevokedWebTokens { get; set; }
        public DbSet<LoginAttemptRecord> LoginAttempts { get; set; }
        public DbSet<ClientActivityBucket> ClientActivityBuckets { get; set; }

        // -- Files & Shares --
        public DbSet<FileMetadata> FileMetadata { get; set; }
        public DbSet<AccessEntry> AccessEntries { get; set; }
        public DbSet<ShareDefinition> ShareDefinitions { get; set; }
        public DbSet<FileVersion> FileVersions { get; set; }
        public DbSet<RecycleBinEntry> RecycleBinEntries { get; set; }
        public DbSet<SambaLifecycleEventReceipt> SambaLifecycleEventReceipts { get; set; }
        public DbSet<ProviderProfile> ProviderProfiles { get; set; }
        public DbSet<StorageConnection> StorageConnections { get; set; }
        public DbSet<CloudAccessShare> CloudAccessShares { get; set; }
        public DbSet<CloudAccessGrant> CloudAccessGrants { get; set; }
        public DbSet<ShareLink> ShareLinks { get; set; }
        public DbSet<StorageAuthorizationTransaction> StorageAuthorizationTransactions { get; set; }
        public DbSet<StorageConnectionCredentialLease> StorageConnectionCredentialLeases { get; set; }
        public DbSet<StorageDeviceAuthorizationSession> StorageDeviceAuthorizationSessions { get; set; }
        public DbSet<SyncDefinition> SyncDefinitions { get; set; }
        public DbSet<SyncDefinitionRuntime> SyncDefinitionRuntimes { get; set; }

        // -- Client API (mobile/desktop apps) --
        public DbSet<SyncDevice> SyncDevices { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<DeviceSyncProfile> DeviceSyncProfiles { get; set; }
        public DbSet<ClientRequestReceipt> ClientRequestReceipts { get; set; }
        public DbSet<FileChangeLogEntry> FileChangeLog { get; set; }

        // -- Local search index (alternative to Elasticsearch) --
        public DbSet<SearchDocumentRecord> SearchDocuments { get; set; }
        public DbSet<SearchTermRecord> SearchTerms { get; set; }

        // -- Departments & Scoped Roles --
        public DbSet<Department> Departments { get; set; }
        public DbSet<DepartmentUser> DepartmentUsers { get; set; }
        public DbSet<ScopedRoleAssignment> ScopedRoleAssignments { get; set; }

        // -- Configuration --
        public DbSet<ConfigSetting> ConfigSettings { get; set; }

        // -- Mail notifications --
        public DbSet<NotificationEvent> NotificationEvents { get; set; }
        public DbSet<MailDelivery> MailDeliveries { get; set; }
        public DbSet<MailRule> MailRules { get; set; }
        public DbSet<MailTemplate> MailTemplates { get; set; }

        // -- File backup (restic) --
        public DbSet<BackupRepository> BackupRepositories { get; set; }
        public DbSet<BackupRepositoryDepartment> BackupRepositoryDepartments { get; set; }
        public DbSet<BackupJob> BackupJobs { get; set; }
        public DbSet<BackupJobSource> BackupJobSources { get; set; }
        public DbSet<BackupRun> BackupRuns { get; set; }
        public DbSet<BackupSnapshot> BackupSnapshots { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureIdentity(modelBuilder);
            ConfigureFilesAndShares(modelBuilder);
            ConfigureStorageConnections(modelBuilder);
            ConfigureSyncDefinitions(modelBuilder);
            ConfigureClientApi(modelBuilder);
            ConfigureMail(modelBuilder);
            ConfigureSearchIndex(modelBuilder);
            ConfigureDepartments(modelBuilder);
            ConfigureBackup(modelBuilder);

            modelBuilder.Entity<ConfigSetting>(entity =>
            {
                entity.ToTable("config_settings");
                entity.HasKey(e => e.Key);
                entity.Property(e => e.Key).HasMaxLength(256);
                entity.Property(e => e.Value).IsRequired();
            });
        }
    }
}
