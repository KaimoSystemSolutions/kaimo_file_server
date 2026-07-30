using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class ScopedRoleAssignmentRepository : IScopedRoleAssignmentRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public ScopedRoleAssignmentRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ScopedRoleAssignment?> GetByIdAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ScopedRoleAssignments.FindAsync(id);
    }

    public async Task<List<ScopedRoleAssignment>> GetByPrincipalAsync(Guid principalId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        
        return await db.ScopedRoleAssignments
            .Where(a => a.PrincipalId == principalId)
            .ToListAsync();
    }

    /// <summary>
    /// Single-query lookup: all assignments referencing a given role.
    /// Replaces the old O(users + groups) iteration in the UI layer.
    /// </summary>
    public async Task<List<ScopedRoleAssignment>> GetByRoleAsync(Guid roleId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.ScopedRoleAssignments
            .Where(a => a.RoleId == roleId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetByScopeAsync(
        ScopeType scopeType, Guid scopeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.ScopedRoleAssignments
            .Where(a => a.ScopeType == scopeType && a.ScopeId == scopeId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetByPrincipalAndScopeAsync(
        Guid principalId, ScopeType scopeType, Guid scopeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.ScopedRoleAssignments
            .Where(a => a.PrincipalId == principalId
                     && a.ScopeType == scopeType
                     && a.ScopeId == scopeId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetEffectiveAssignmentsAsync(
        Guid userId, IEnumerable<Guid> groupIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var allPrincipalIds = new List<Guid> { userId };
        allPrincipalIds.AddRange(groupIds);

        return await db.ScopedRoleAssignments
            .Where(a => allPrincipalIds.Contains(a.PrincipalId))
            .ToListAsync();
    }

    public async Task<ScopedRoleAssignment> CreateAsync(ScopedRoleAssignment assignment)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.ScopedRoleAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entry = await db.ScopedRoleAssignments.FindAsync(id);
        if (entry != null)
        {
            db.ScopedRoleAssignments.Remove(entry);
            await db.SaveChangesAsync();
        }
    }

    public async Task DeleteByScopeAsync(ScopeType scopeType, Guid scopeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entries = await db.ScopedRoleAssignments
            .Where(a => a.ScopeType == scopeType && a.ScopeId == scopeId)
            .ToListAsync();

        db.ScopedRoleAssignments.RemoveRange(entries);
        await db.SaveChangesAsync();
    }
}