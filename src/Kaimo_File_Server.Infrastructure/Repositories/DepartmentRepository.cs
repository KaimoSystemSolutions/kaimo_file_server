using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class DepartmentRepository : IDepartmentRepository
{
    private readonly ApplicationDbContext _db;

    public DepartmentRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Department?> GetByIdAsync(Guid id)
        => await _db.Departments.FindAsync(id);

    public async Task<Department?> GetByNameAsync(string name)
        => await _db.Departments.FirstOrDefaultAsync(d => d.Name == name);

    public async Task<List<Department>> GetAllAsync()
        => await _db.Departments.OrderBy(d => d.Name).ToListAsync();

    public async Task<List<Department>> GetChildrenAsync(Guid parentId)
        => await _db.Departments
            .Where(d => d.ParentDepartmentId == parentId)
            .OrderBy(d => d.Name)
            .ToListAsync();

    public async Task<Department> CreateAsync(Department department)
    {
        _db.Departments.Add(department);
        await _db.SaveChangesAsync();
        return department;
    }

    public async Task UpdateAsync(Department department)
    {
        _db.Departments.Update(department);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var dept = await _db.Departments.FindAsync(id);
        if (dept != null)
        {
            _db.Departments.Remove(dept);
            await _db.SaveChangesAsync();
        }
    }

    // ── User ↔ Department ──

    public async Task<List<User>> GetUsersAsync(Guid departmentId)
    {
        return await _db.DepartmentUsers
            .Where(du => du.DepartmentId == departmentId)
            .Join(_db.Users, du => du.UserId, u => u.Id, (_, u) => u)
            .OrderBy(u => u.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetDepartmentsForUserAsync(Guid userId)
    {
        return await _db.DepartmentUsers
            .Where(du => du.UserId == userId)
            .Join(_db.Departments, du => du.DepartmentId, d => d.Id, (_, d) => d)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task AddUserAsync(Guid departmentId, Guid userId)
    {
        var exists = await _db.DepartmentUsers.AnyAsync(
            du => du.DepartmentId == departmentId && du.UserId == userId);
        if (!exists)
        {
            _db.DepartmentUsers.Add(new DepartmentUser(departmentId, userId));
            await _db.SaveChangesAsync();
        }
    }

    public async Task RemoveUserAsync(Guid departmentId, Guid userId)
    {
        var entry = await _db.DepartmentUsers.FirstOrDefaultAsync(
            du => du.DepartmentId == departmentId && du.UserId == userId);
        if (entry != null)
        {
            _db.DepartmentUsers.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> IsUserInDepartmentAsync(Guid userId, Guid departmentId)
    {
        return await _db.DepartmentUsers.AnyAsync(
            du => du.UserId == userId && du.DepartmentId == departmentId);
    }

    // ── Group ↔ Department ──

    public async Task<List<Group>> GetGroupsAsync(Guid departmentId)
    {
        return await _db.DepartmentGroups
            .Where(dg => dg.DepartmentId == departmentId)
            .Join(_db.Groups, dg => dg.GroupId, g => g.Id, (_, g) => g)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetDepartmentsForGroupAsync(Guid groupId)
    {
        return await _db.DepartmentGroups
            .Where(dg => dg.GroupId == groupId)
            .Join(_db.Departments, dg => dg.DepartmentId, d => d.Id, (_, d) => d)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task AddGroupAsync(Guid departmentId, Guid groupId)
    {
        var exists = await _db.DepartmentGroups.AnyAsync(
            dg => dg.DepartmentId == departmentId && dg.GroupId == groupId);
        if (!exists)
        {
            _db.DepartmentGroups.Add(new DepartmentGroup(departmentId, groupId));
            await _db.SaveChangesAsync();
        }
    }

    public async Task RemoveGroupAsync(Guid departmentId, Guid groupId)
    {
        var entry = await _db.DepartmentGroups.FirstOrDefaultAsync(
            dg => dg.DepartmentId == departmentId && dg.GroupId == groupId);
        if (entry != null)
        {
            _db.DepartmentGroups.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> IsGroupInDepartmentAsync(Guid groupId, Guid departmentId)
    {
        return await _db.DepartmentGroups.AnyAsync(
            dg => dg.GroupId == groupId && dg.DepartmentId == departmentId);
    }

    // ── Share ↔ Department ──

    public async Task<List<ShareDefinition>> GetSharesAsync(Guid departmentId)
    {
        return await _db.DepartmentShares
            .Where(ds => ds.DepartmentId == departmentId)
            .Join(_db.ShareDefinitions, ds => ds.ShareId, s => s.Id, (_, s) => s)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetDepartmentsForShareAsync(Guid shareId)
    {
        return await _db.DepartmentShares
            .Where(ds => ds.ShareId == shareId)
            .Join(_db.Departments, ds => ds.DepartmentId, d => d.Id, (_, d) => d)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task AddShareAsync(Guid departmentId, Guid shareId)
    {
        var exists = await _db.DepartmentShares.AnyAsync(
            ds => ds.DepartmentId == departmentId && ds.ShareId == shareId);
        if (!exists)
        {
            _db.DepartmentShares.Add(new DepartmentShare(departmentId, shareId));
            await _db.SaveChangesAsync();
        }
    }

    public async Task RemoveShareAsync(Guid departmentId, Guid shareId)
    {
        var entry = await _db.DepartmentShares.FirstOrDefaultAsync(
            ds => ds.DepartmentId == departmentId && ds.ShareId == shareId);
        if (entry != null)
        {
            _db.DepartmentShares.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> IsShareInDepartmentAsync(Guid shareId, Guid departmentId)
    {
        return await _db.DepartmentShares.AnyAsync(
            ds => ds.ShareId == shareId && ds.DepartmentId == departmentId);
    }
}