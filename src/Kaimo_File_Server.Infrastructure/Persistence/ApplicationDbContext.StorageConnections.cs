using Kaimo_File_Server.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Provider profiles, storage connections, cloud access, share links and authorization state.</summary>
    private void ConfigureStorageConnections(ModelBuilder modelBuilder)
    {
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
            entity.Property(x => x.AllowedExtensions).HasMaxLength(1000);
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
    }
}
