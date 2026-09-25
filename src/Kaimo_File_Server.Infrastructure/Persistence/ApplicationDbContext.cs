using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
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

        // -- Departments & Scoped Roles --
        public DbSet<Department> Departments { get; set; }
        public DbSet<DepartmentUser> DepartmentUsers { get; set; }
        public DbSet<ScopedRoleAssignment> ScopedRoleAssignments { get; set; }

        // -- Configuration --
        public DbSet<ConfigSetting> ConfigSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // -- Identity base (TPC) --

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
                // Wide enough for the encrypted form ("enc:" + Base64(nonce|tag|ciphertext)), while
                // still holding legacy 32-char plaintext hex.
                entity.Property(e => e.NtHash).IsRequired().HasMaxLength(256);
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.Email).HasMaxLength(254);
                entity.Property(e => e.FirstName).HasMaxLength(100);
                entity.Property(e => e.LastName).HasMaxLength(100);
                // Photo is stored as bytea; upload size is capped in the application layer.
                entity.Property(e => e.PhotoContentType).HasMaxLength(100);
                entity.Property(e => e.IsEnabled).IsRequired().HasDefaultValue(true);
                entity.Property(e => e.CanChangePassword).IsRequired().HasDefaultValue(true);
                entity.Property(e => e.SecurityStamp).IsRequired().HasMaxLength(64);
                entity.Property(e => e.MustChangePassword).IsRequired();
            });

            modelBuilder.Entity<RevokedWebToken>(entity =>
            {
                entity.ToTable("revoked_web_tokens");
                entity.HasKey(e => e.Jti);
                entity.Property(e => e.Jti).HasMaxLength(64);
                // Drives retention pruning of expired entries.
                entity.HasIndex(e => e.ExpiresAtUtc);
            });

            // -- Security overview: login attempts and hourly API/WebDAV activity per client --
            modelBuilder.Entity<LoginAttemptRecord>(entity =>
            {
                entity.ToTable("login_attempts");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
                entity.Property(e => e.Username).IsRequired().HasMaxLength(256);
                entity.Property(e => e.Address).HasMaxLength(64);
                // Stored as text so the table stays readable for ad-hoc SQL analysis.
                entity.Property(e => e.Channel).IsRequired().HasConversion<string>().HasMaxLength(16);
                entity.Property(e => e.Outcome).IsRequired().HasConversion<string>().HasMaxLength(32);
                // Period filter and retention pruning; per-account history.
                entity.HasIndex(e => e.AtUtc);
                entity.HasIndex(e => new { e.Username, e.AtUtc });
                entity.HasIndex(e => e.LockedUntilUtc);
            });

            modelBuilder.Entity<ClientActivityBucket>(entity =>
            {
                entity.ToTable("client_activity");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
                entity.Property(e => e.Address).IsRequired().HasMaxLength(64);
                entity.Property(e => e.LastPath).IsRequired().HasMaxLength(512);
                entity.HasIndex(e => new { e.Address, e.HourUtc }).IsUnique();
                entity.HasIndex(e => e.LastSeenUtc);
            });

            modelBuilder.Entity<Group>(entity =>
            {
                entity.ToTable("groups");

                // Direct FK to Department (required, default = Global)
                entity.Property(e => e.DepartmentId)
                    .IsRequired()
                    .HasDefaultValue(WellKnownGUIDs.DEPARTMENT_GLOBAL);

                entity.HasIndex(e => e.DepartmentId);
            });

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

            // -- Files & Shares --

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

            modelBuilder.Entity<ProviderProfile>(entity =>
            {
                entity.ToTable("provider_profiles");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.ProviderId).IsRequired().HasMaxLength(50);
                entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
                entity.Property(x => x.AuthorizationMode).HasConversion<int>();
                entity.Property(x => x.TenantOrOrganizationId).HasMaxLength(300);
                entity.Property(x => x.PublicClientId).HasMaxLength(500);
                entity.Property(x => x.SecretReference).HasMaxLength(1000);
                entity.Property(x => x.AllowedRedirectBaseUri).HasMaxLength(2000);
                entity.Property(x => x.AllowedScopes).HasColumnType("text");
                entity.HasIndex(x => x.Name).IsUnique();
                entity.HasIndex(x => new { x.ProviderId, x.Enabled });
            });

            modelBuilder.Entity<StorageConnection>(entity =>
            {
                entity.ToTable("storage_connections");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.ProviderId).IsRequired().HasMaxLength(50);
                entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
                entity.Property(x => x.AuthorizationMode).HasConversion<int>();
                entity.Property(x => x.SettingsJson).HasColumnType("text");
                entity.Property(x => x.AccountDisplayName).HasMaxLength(300);
                entity.Property(x => x.AccountEmail).HasMaxLength(320);
                entity.Property(x => x.ProviderAccountId).HasMaxLength(500);
                entity.Property(x => x.ProviderTenantId).HasMaxLength(500);
                entity.Property(x => x.ProviderSubjectId).HasMaxLength(500);
                entity.Property(x => x.EffectiveScopes).HasColumnType("text");
                entity.Property(x => x.EncryptedCredentialPayload).HasColumnType("text");
                entity.Property(x => x.State).HasConversion<int>();
                // Kept at the legacy width during the additive migration. New
                // writes contain only sanitized provider error codes.
                entity.Property(x => x.LastErrorCode).HasMaxLength(2000);
                entity.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();
                entity.HasIndex(x => x.Name).IsUnique();
                entity.HasIndex(x => x.ProviderProfileId);
                entity.HasOne<ProviderProfile>().WithMany().HasForeignKey(x => x.ProviderProfileId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<CloudAccessShare>(entity =>
            {
                entity.ToTable("cloud_access_shares");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
                entity.Property(x => x.Description).HasMaxLength(1000);
                entity.Property(x => x.RemoteRootPath).IsRequired().HasMaxLength(2000);
                entity.Property(x => x.RemoteRootItemId).HasMaxLength(500);
                entity.HasIndex(x => x.Name).IsUnique();
                entity.HasIndex(x => x.DepartmentId);
                entity.HasIndex(x => x.ConnectionId);
                entity.HasOne<StorageConnection>().WithMany().HasForeignKey(x => x.ConnectionId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<CloudAccessGrant>(entity =>
            {
                entity.ToTable("cloud_access_grants");
                entity.HasKey(x => new { x.ShareId, x.PrincipalId });
                entity.Property(x => x.Permission).HasConversion<int>();
                entity.HasIndex(x => x.PrincipalId);
                entity.HasOne<CloudAccessShare>().WithMany().HasForeignKey(x => x.ShareId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ShareLink>(entity =>
            {
                entity.ToTable("share_links");
                entity.HasKey(x => x.Id);
                // The plain token is never persisted; lookups go through its hash.
                entity.Ignore(x => x.Token);
                entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
                entity.HasIndex(x => x.TokenHash).IsUnique();
                entity.Property(x => x.ProtectedToken).HasColumnType("text");
                // Pre-hashing column, kept under its original name until the backfill empties it.
                entity.Property(x => x.LegacyToken).HasColumnName("Token").HasMaxLength(128);
                entity.HasIndex(x => x.LegacyToken).IsUnique();
                entity.Property(x => x.RootRelativePath).IsRequired().HasMaxLength(2000);
                entity.Property(x => x.DisplayName).IsRequired().HasMaxLength(400);
                entity.Property(x => x.PasswordHash).HasMaxLength(200);
                entity.Property(x => x.BaseAddress).HasMaxLength(2000);
                entity.Property(x => x.ShareId).IsRequired();
                entity.Property(x => x.CreatedByUserId).IsRequired();
                entity.HasIndex(x => x.ShareId);
                // No FK cascade from shares: links are pruned through their own admin flow, and a
                // dangling link simply resolves to not-found when its share or path is gone.
            });

            modelBuilder.Entity<StorageAuthorizationTransaction>(entity =>
            {
                entity.ToTable("storage_authorization_transactions");
                entity.HasKey(x => x.TokenHash);
                entity.Property(x => x.TokenHash).HasMaxLength(64);
                entity.Property(x => x.ResourcePath).IsRequired().HasMaxLength(2000);
                entity.Property(x => x.ProviderId).IsRequired().HasMaxLength(100);
                entity.Property(x => x.ProtectedContext).HasColumnType("text");
                entity.HasIndex(x => x.ExpiresAtUtc);
                entity.HasIndex(x => new { x.ResourceId, x.ProviderId });
            });

            modelBuilder.Entity<StorageConnectionCredentialLease>(entity =>
            {
                entity.ToTable("storage_connection_credential_leases");
                entity.HasKey(x => x.ConnectionId);
                entity.HasIndex(x => x.ExpiresAtUtc);
                entity.HasOne<StorageConnection>().WithOne().HasForeignKey<StorageConnectionCredentialLease>(x => x.ConnectionId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<StorageDeviceAuthorizationSession>(entity =>
            {
                entity.ToTable("storage_device_authorization_sessions");
                entity.HasKey(x => x.SessionHash);
                entity.Property(x => x.SessionHash).HasMaxLength(64);
                entity.Property(x => x.ProviderId).IsRequired().HasMaxLength(100);
                entity.Property(x => x.ProtectedPayload).IsRequired().HasColumnType("text");
                entity.HasIndex(x => x.ExpiresAtUtc);
                entity.HasIndex(x => x.PollLeaseUntilUtc);
            });

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

            // -- Client API (mobile/desktop apps) --

            modelBuilder.Entity<SyncDevice>(entity =>
            {
                entity.ToTable("sync_devices");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.UserId).IsRequired();
                entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Platform).HasMaxLength(50);
                entity.Property(e => e.PushToken).HasMaxLength(500);
                entity.HasIndex(e => e.UserId);
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

            // -- Departments --

            modelBuilder.Entity<Department>(entity =>
            {
                entity.ToTable("departments");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.Property(e => e.Description).HasMaxLength(500);
                entity.Property(e => e.ParentDepartmentId);

                entity.Property(e => e.DefaultFilePermission)
                    .HasColumnName("default_file_permission")
                    .IsRequired(false);

                entity.Property(e => e.Color).HasMaxLength(9).IsRequired(false);

                entity.HasIndex(e => e.ParentDepartmentId);
            });

            modelBuilder.Entity<DepartmentUser>(entity =>
            {
                entity.ToTable("department_users");
                entity.HasKey(e => new { e.DepartmentId, e.UserId });
                entity.HasIndex(e => e.UserId);
            });

            // -- Scoped Role Assignments --

            modelBuilder.Entity<ScopedRoleAssignment>(entity =>
            {
                entity.ToTable("scoped_role_assignments");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ScopeType).HasConversion<int>();

                entity.HasIndex(e => new { e.PrincipalId, e.RoleId, e.ScopeType, e.ScopeId })
                    .IsUnique();

                entity.HasIndex(e => e.PrincipalId);
                entity.HasIndex(e => new { e.ScopeType, e.ScopeId });
                entity.HasIndex(e => e.RoleId);
            });

            // -- Configuration --

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
