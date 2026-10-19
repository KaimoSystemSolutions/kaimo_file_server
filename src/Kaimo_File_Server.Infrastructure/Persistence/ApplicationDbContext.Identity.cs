using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Users, groups, roles, token revocation and security-overview tables.</summary>
    private void ConfigureIdentity(ModelBuilder modelBuilder)
    {
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
            entity.Property(e => e.HomeDirectoryEnabled).IsRequired().HasDefaultValue(true);
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
    }
}
