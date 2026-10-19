using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Departments, department membership and scoped role assignments.</summary>
    private void ConfigureDepartments(ModelBuilder modelBuilder)
    {
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
    }
}
