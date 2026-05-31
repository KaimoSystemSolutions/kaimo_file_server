using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

public class ScopedRoleAssignmentRepository : IScopedRoleAssignmentRepository
{
    private readonly ApplicationDbContext _db;

    public ScopedRoleAssignmentRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ScopedRoleAssignment?> GetByIdAsync(Guid id)
        => await _db.ScopedRoleAssignments.FindAsync(id);

    public async Task<List<ScopedRoleAssignment>> GetByPrincipalAsync(Guid principalId)
    {
        return await _db.ScopedRoleAssignments
            .Where(a => a.PrincipalId == principalId)
            .ToListAsync();
    }

    /// <summary>
    /// Single-query lookup: all assignments referencing a given role.
    /// Replaces the old O(users + groups) iteration in the UI layer.
    /// </summary>
    public async Task<List<ScopedRoleAssignment>> GetByRoleAsync(Guid roleId)
    {
        return await _db.ScopedRoleAssignments
            .Where(a => a.RoleId == roleId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetByScopeAsync(
        ScopeType scopeType, Guid scopeId)
    {
        return await _db.ScopedRoleAssignments
            .Where(a => a.ScopeType == scopeType && a.ScopeId == scopeId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetByPrincipalAndScopeAsync(
        Guid principalId, ScopeType scopeType, Guid scopeId)
    {
        return await _db.ScopedRoleAssignments
            .Where(a => a.PrincipalId == principalId
                     && a.ScopeType == scopeType
                     && a.ScopeId == scopeId)
            .ToListAsync();
    }

    public async Task<List<ScopedRoleAssignment>> GetEffectiveAssignmentsAsync(
        Guid userId, IEnumerable<Guid> groupIds)
    {
        var allPrincipalIds = new List<Guid> { userId };
        allPrincipalIds.AddRange(groupIds);

        return await _db.ScopedRoleAssignments
            .Where(a => allPrincipalIds.Contains(a.PrincipalId))
            .ToListAsync();
    }

    public async Task<ScopedRoleAssignment> CreateAsync(ScopedRoleAssignment assignment)
    {
        _db.ScopedRoleAssignments.Add(assignment);
        await _db.SaveChangesAsync();
        return assignment;
    }

    public async Task DeleteAsync(Guid id)
    {
        var entry = await _db.ScopedRoleAssignments.FindAsync(id);
        if (entry != null)
        {
            _db.ScopedRoleAssignments.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task DeleteByScopeAsync(ScopeType scopeType, Guid scopeId)
    {
        var entries = await _db.ScopedRoleAssignments
            .Where(a => a.ScopeType == scopeType && a.ScopeId == scopeId)
            .ToListAsync();

        _db.ScopedRoleAssignments.RemoveRange(entries);
        await _db.SaveChangesAsync();
    }
}