using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class DepartmentRepository : IDepartmentRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public DepartmentRepository(IDbContextFactory<ApplicationDbContext> db)
    {
        _dbFactory = db;
    }

    // ══════════════════════════════════════════
    //  CRUD
    // ══════════════════════════════════════════

    public async Task<Department?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Departments.FindAsync(id);
    } 

    public async Task<Department?> GetByNameAsync(string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Departments.FirstOrDefaultAsync(d => d.Name == name);
    } 

    public async Task<List<Department>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Departments.OrderBy(d => d.Name).ToListAsync();
    } 
    
    public async Task<Department> CreateAsync(Department department)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department;
    }

    public async Task UpdateAsync(Department department)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.Departments.Update(department);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var dept = await db.Departments.FindAsync(id);
        if (dept != null)
        {
            db.Departments.Remove(dept);
            await db.SaveChangesAsync();
        }
        
    }

    // ══════════════════════════════════════════
    //  Hierarchy
    // ══════════════════════════════════════════

    public async Task<List<Department>> GetChildrenAsync(Guid parentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Departments
            .Where(d => d.ParentDepartmentId == parentId)
            .OrderBy(d => d.Name)
            .ToListAsync();
    } 

    /// <inheritdoc />
    public async Task<List<Department>> GetAncestorChainAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var ancestors = new List<Department>();
        var visited = new HashSet<Guid> { departmentId }; // cycle protection

        var current = await db.Departments.FindAsync(departmentId);
        if (current == null) return ancestors;

        var parentId = current.ParentDepartmentId;

        while (parentId.HasValue && !visited.Contains(parentId.Value))
        {
            visited.Add(parentId.Value);
            var parent = await db.Departments.FindAsync(parentId.Value);
            if (parent == null) break;

            ancestors.Add(parent);
            parentId = parent.ParentDepartmentId;
        }

        return ancestors; // ordered: immediate parent → root
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetDescendantIdsAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(departmentId);

        // Load all departments once to avoid N+1
        var allDepts = await db.Departments
            .Select(d => new { d.Id, d.ParentDepartmentId })
            .ToListAsync();

        var childrenLookup = allDepts
            .Where(d => d.ParentDepartmentId.HasValue)
            .GroupBy(d => d.ParentDepartmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToList());

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (childrenLookup.TryGetValue(current, out var children))
            {
                foreach (var childId in children)
                {
                    if (result.Add(childId)) // cycle protection
                        queue.Enqueue(childId);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<Department>> GetDescendantsAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var ids = await GetDescendantIdsAsync(departmentId);
        if (ids.Count == 0) return [];

        return await db.Departments
            .Where(d => ids.Contains(d.Id))
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    // ══════════════════════════════════════════
    //  User ↔ Department
    // ══════════════════════════════════════════

    public async Task<List<User>> GetUsersAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.DepartmentUsers
            .Where(du => du.DepartmentId == departmentId)
            .Join(db.Users, du => du.UserId, u => u.Id, (_, u) => u)
            .OrderBy(u => u.Name)
            .ToListAsync();
    }

    public async Task<List<Department>> GetDepartmentsForUserAsync(Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.DepartmentUsers
            .Where(du => du.UserId == userId)
            .Join(db.Departments, du => du.DepartmentId, d => d.Id, (_, d) => d)
            .OrderBy(d => d.Name)
            .ToListAsync();
    }

    public async Task AddUserAsync(Guid departmentId, Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.DepartmentUsers.AnyAsync(
            du => du.DepartmentId == departmentId && du.UserId == userId);
        if (!exists)
        {
            db.DepartmentUsers.Add(new DepartmentUser(departmentId, userId));
            await db.SaveChangesAsync();
        }
    }

    public async Task RemoveUserAsync(Guid departmentId, Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entry = await db.DepartmentUsers.FirstOrDefaultAsync(
            du => du.DepartmentId == departmentId && du.UserId == userId);
        if (entry != null)
        {
            db.DepartmentUsers.Remove(entry);
            await db.SaveChangesAsync();
        }
    }

    public async Task<bool> IsUserInDepartmentAsync(Guid userId, Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.DepartmentUsers.AnyAsync(
            du => du.UserId == userId && du.DepartmentId == departmentId);
    }

    /// <inheritdoc />
    public async Task<bool> IsUserInDepartmentOrDescendantAsync(Guid userId, Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check direct membership first (fast path)
        if (await IsUserInDepartmentAsync(userId, departmentId))
            return true;

        // Check descendant departments
        var descendantIds = await GetDescendantIdsAsync(departmentId);
        if (descendantIds.Count == 0) return false;

        return await db.DepartmentUsers.AnyAsync(
            du => du.UserId == userId && descendantIds.Contains(du.DepartmentId));
    }

    // ══════════════════════════════════════════
    //  Group ↔ Department
    // ══════════════════════════════════════════

    public async Task<List<Group>> GetGroupsAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Groups
            .Where(group => group.DepartmentId == departmentId)
            .OrderBy(group => group.Name)
            .ToListAsync();
    }

    public async Task<bool> IsGroupInDepartmentAsync(Guid groupId, Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Groups.AnyAsync(
            group => group.Id == groupId && group.DepartmentId == departmentId);
    }

    // ══════════════════════════════════════════
    //  Share ↔ Department
    // ══════════════════════════════════════════

    public async Task<List<ShareDefinition>> GetSharesAsync(Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.ShareDefinitions
            .Where(share => share.DepartmentId == departmentId)
            .OrderBy(share => share.Name)
            .ToListAsync();
    }

    public async Task<bool> IsShareInDepartmentAsync(Guid shareId, Guid departmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.ShareDefinitions.AnyAsync(
            share => share.Id == shareId && share.DepartmentId == departmentId);
    }
}