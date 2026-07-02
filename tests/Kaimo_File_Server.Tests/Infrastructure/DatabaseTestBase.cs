using System.Security.Claims;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Infrastructure.Repositories;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Kaimo_File_Server.Tests.Infrastructure;

/// <summary>
/// Base class for view-model regression tests that run against a REAL database.
///
/// Concept
/// ───────
/// View models are the hinge between the UI and persistence: their methods claim
/// to create shares, rename users, toggle flags, grant access. A mock-only test can
/// only assert "the repository was called"; it cannot catch a method that calls the
/// repository with the wrong value, forgets to save, or saves to the wrong row.
///
/// This harness closes that gap. It stands up an in-memory Sqlite database with the
/// production EF schema, wires the view model to the REAL repositories, runs the
/// method under test, and then re-reads the row through a FRESH context
/// (<see cref="NewContext"/>) to assert the value that actually landed in the store.
///
/// Sqlite (not the EF InMemory provider) is used deliberately: it is relational, so
/// it honours unique indexes, required columns, the TPC identity hierarchy, and
/// cascade deletes — the same constraints production runs under.
///
/// What stays mocked: authorization (<see cref="IManagementAuthService"/>), actor
/// resolution (<see cref="IUserContextFactory"/>) and the filesystem
/// (<c>IStorageEngine</c>). Those have their own dedicated test suites; here we want
/// to isolate the question "does the view-model method persist the right value?".
/// </summary>
public abstract class DatabaseTestBase : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Production-shaped context factory over the shared connection.</summary>
    protected TestDbContextFactory DbFactory { get; }

    protected DatabaseTestBase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DbFactory = new TestDbContextFactory(_connection);

        using var db = DbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─────────────────────── Assertion context ───────────────────────

    /// <summary>
    /// A brand-new context for reading back what is ACTUALLY in the store.
    /// Never reuse the context a mutation ran on for assertions — that would read
    /// EF's change-tracker cache and could pass even if nothing was persisted.
    /// </summary>
    protected ApplicationDbContext NewContext() => DbFactory.CreateDbContext();

    // ─────────────────────── Real repositories ───────────────────────
    // Repos that take a bare ApplicationDbContext get their own fresh context;
    // because every context shares one connection, their SaveChanges is visible
    // to any later NewContext() used for assertions.

    protected ShareRepository ShareRepo() => new(DbFactory);
    protected UserRepository UserRepo() => new(NewContext());
    protected GroupRepository GroupRepo() => new(NewContext());
    protected RoleRepository RoleRepo() => new(NewContext());
    protected AclRepository AclRepo() => new(NewContext());
    protected DepartmentRepository DepartmentRepo() => new(NewContext());
    protected FileMetadataRepository FileMetadataRepo() => new(NewContext());
    protected ScopedRoleAssignmentRepository ScopedRoleRepo() => new(NewContext());

    // ─────────────────────── Real ACL evaluation ───────────────────────

    /// <summary>
    /// A real <see cref="AclService"/> wired to the real repositories over this test's
    /// database. Use it to assert the ACTUAL authorization outcome after a mutation —
    /// e.g. "delete this permission, then confirm access is really denied" — instead of
    /// trusting a mocked decision.
    /// </summary>
    protected AclService BuildAclService() => new(BuildRepoServiceProvider());

    /// <summary>
    /// A real <see cref="ManagementAuthService"/> over this test's database. Use it to
    /// assert how a management decision (e.g. "can this actor manage this department?")
    /// changes after a role permission or scoped assignment is added or removed.
    /// </summary>
    protected ManagementAuthService BuildManagementAuthService()
        => new(ScopedRoleRepo(), DepartmentRepo(), RoleRepo(), GroupRepo(), ShareRepo());

    private IServiceProvider BuildRepoServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(DbFactory);
        services.AddTransient(_ => DbFactory.CreateDbContext());
        services.AddTransient<IAclRepository>(sp => new AclRepository(sp.GetRequiredService<ApplicationDbContext>()));
        services.AddTransient<IShareRepository>(_ => new ShareRepository(DbFactory));
        services.AddTransient<IDepartmentRepository>(sp => new DepartmentRepository(sp.GetRequiredService<ApplicationDbContext>()));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Seeds a <see cref="FileMetadata"/> row at <paramref name="path"/> together with its
    /// ACL entries (their <c>FileMetadataId</c> is wired automatically). This is the shape
    /// <see cref="AclService.HasAccessAsync"/> reads back through the repository.
    /// </summary>
    protected FileMetadata SeedFileWithAcl(
        Guid shareId, string path, bool isDirectory, Guid ownerId, params AccessEntry[] entries)
    {
        var meta = new FileMetadata
        {
            Id = Guid.NewGuid(),
            ShareId = shareId,
            Path = ShareRelativePath.Normalize(path),
            Name = path,
            IsDirectory = isDirectory,
            Size = 0,
            OwnerId = ownerId,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            Acl = entries.ToList(),
        };
        foreach (var e in entries) e.FileMetadataId = meta.Id;

        using var db = NewContext();
        db.FileMetadata.Add(meta);
        db.SaveChanges();
        return meta;
    }

    // ─────────────────────── Seed helpers ───────────────────────

    protected User SeedUser(
        string username, string? displayName = null,
        bool isEnabled = true, Guid? id = null)
    {
        var user = new User(
            id ?? Guid.NewGuid(),
            displayName ?? username,
            username,
            passwordHash: "pw-hash",
            ntHash: "nt-hash",
            isEnabled: isEnabled);

        using var db = NewContext();
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    protected ShareDefinition SeedShare(
        string name, string? path = null,
        bool isEnabled = true, bool isHidden = false,
        bool isRecycleEnabled = false, Guid? departmentId = null)
    {
        var share = new ShareDefinition(
            name, path ?? $"/data/{name}", departmentId,
            isEnabled, isHidden, isRecycleEnabled);

        using var db = NewContext();
        db.ShareDefinitions.Add(share);
        db.SaveChanges();
        return share;
    }

    protected Department SeedDepartment(
        string name, Guid? parentId = null, long? defaultFilePermission = null, Guid? id = null)
    {
        var dept = new Department(name, parentDepartmentId: parentId)
        {
            DefaultFilePermission = defaultFilePermission,
        };
        if (id is not null) dept.Id = id.Value;

        using var db = NewContext();
        db.Departments.Add(dept);
        db.SaveChanges();
        return dept;
    }

    // ─────────────────────── Auth / actor doubles ───────────────────────

    /// <summary>
    /// An <see cref="AuthenticationStateProvider"/> that reports the given user as
    /// the signed-in principal (name claim). Pass <c>null</c> for an anonymous state.
    /// </summary>
    protected static AuthenticationStateProvider AuthStateFor(string? username)
    {
        var identity = username is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, username) }, authenticationType: "test");

        var mock = new Mock<AuthenticationStateProvider>();
        mock.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        return mock.Object;
    }

    /// <summary>
    /// A <see cref="IUserContextFactory"/> that resolves the given usernames to a
    /// <see cref="UserContext"/> (no roles/groups/departments unless supplied).
    /// </summary>
    protected static Mock<IUserContextFactory> UserContextFactoryFor(
        params (string Username, UserContext Context)[] contexts)
    {
        var mock = new Mock<IUserContextFactory>();
        foreach (var (username, ctx) in contexts)
            mock.Setup(f => f.CreateByUsernameAsync(username)).ReturnsAsync(ctx);
        return mock;
    }

    protected static UserContext ContextFor(User user, params Department[] departments)
        => new(user, [], [], [], departments.ToHashSet());
}
