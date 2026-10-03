using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Provisions homes with the real <see cref="HomeDirectoryService"/> against in-memory
/// repositories and evaluates the resulting entries with the real <see cref="AclService"/>,
/// so the tests cover the actual ACL schema rather than a restatement of it.
/// </summary>
public sealed class HomeDirectoryServiceTests : IDisposable
{
    private const FilePermission ReadWrite = FilePermission.ReadAll | FilePermission.WriteAll;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-homes-" + Guid.NewGuid().ToString("N"));
    private readonly List<ShareDefinition> _shares = [];
    private readonly List<FileMetadata> _meta = [];
    private readonly List<AccessEntry> _acl = [];
    private readonly List<User> _users = [];
    private readonly List<ShareLink> _links = [];
    private readonly List<FileChangeLogEntry> _changes = [];
    private readonly List<string> _deletedVersionPaths = [];
    private readonly Department _dept;
    private readonly HomeDirectoryService _sut;
    private readonly AclService _aclService;

    public HomeDirectoryServiceTests()
    {
        Directory.CreateDirectory(_root);

        // Every user is a member of a department whose default grants read/write: the home
        // share must ignore it, otherwise every member could reach every home.
        _dept = new Department("Everyone-RW") { DefaultFilePermission = (long)ReadWrite };

        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _shares.ToList());
        shares.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _shares.FirstOrDefault(s => s.Id == id));
        shares.Setup(r => r.GetByNameAsync(It.IsAny<string>()))
            .ReturnsAsync((string n) => _shares.FirstOrDefault(s => s.Name == n));
        shares.Setup(r => r.CreateAsync(It.IsAny<ShareDefinition>()))
            .ReturnsAsync((ShareDefinition s) => { _shares.Add(s); return s; });

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _users.ToList());
        users.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _users.FirstOrDefault(u => u.Id == id));
        users.Setup(r => r.UpdateHomeDirectoryEnabledAsync(It.IsAny<Guid>(), It.IsAny<bool>()))
            .Returns((Guid id, bool enabled) => { _users.Single(u => u.Id == id).HomeDirectoryEnabled = enabled; return Task.CompletedTask; });

        var metadata = new Mock<IFileMetadataRepository>();
        metadata.Setup(r => r.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync((string path, bool isDir, Guid owner, Guid shareId) =>
            {
                var existing = _meta.FirstOrDefault(m => m.ShareId == shareId && m.Path == path);
                if (existing is not null) return existing;
                var created = new FileMetadata { Id = Guid.NewGuid(), ShareId = shareId, Path = path, IsDirectory = isDir, OwnerId = owner };
                _meta.Add(created);
                return created;
            });

        var acls = new Mock<IAclRepository>();
        acls.Setup(r => r.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _acl.Where(e => e.FileMetadataId == id).ToList());
        acls.Setup(r => r.AddAsync(It.IsAny<AccessEntry>()))
            .ReturnsAsync((AccessEntry e) => { _acl.Add(e); return e; });
        acls.Setup(r => r.DeleteAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => { _acl.RemoveAll(e => e.Id == id); return Task.CompletedTask; });
        acls.Setup(r => r.GetAclsForPathsAsync(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync((Guid shareId, List<string> paths) => _meta
                .Where(m => m.ShareId == shareId && paths.Contains(m.Path))
                .Select(m => (m.Path, m.IsDirectory, _acl.Where(e => e.FileMetadataId == m.Id).ToList()))
                .ToList());

        var depts = new Mock<IDepartmentRepository>();
        depts.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(_dept);
        depts.Setup(r => r.GetAncestorChainAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Department>());
        depts.Setup(r => r.GetDescendantIdsAsync(It.IsAny<Guid>())).ReturnsAsync(new HashSet<Guid> { _dept.Id });

        var services = new ServiceCollection();
        services.AddSingleton(shares.Object);
        services.AddSingleton(depts.Object);
        services.AddSingleton(acls.Object);
        _aclService = new AclService(services.BuildServiceProvider());

        acls.Setup(r => r.DeleteFileMetadataPathsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((Guid shareId, string path) =>
            {
                var gone = _meta.Where(m => m.ShareId == shareId
                    && (m.Path == path || m.Path.StartsWith(path + "/"))).ToList();
                foreach (var m in gone)
                {
                    _meta.Remove(m);
                    _acl.RemoveAll(e => e.FileMetadataId == m.Id);   // database cascade
                }
                return gone.Count;
            });

        var versions = new Mock<IFileVersionService>();
        versions.Setup(v => v.DeletePathAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string path) => { _deletedVersionPaths.Add(path); return 0; });
        var links = new Mock<IShareLinkRepository>();
        links.Setup(r => r.ListForSharesAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(() => _links.ToList());
        links.Setup(r => r.DeleteAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => { _links.RemoveAll(l => l.Id == id); return Task.CompletedTask; });
        var changes = new Mock<IFileChangeLogRepository>();
        changes.Setup(r => r.AppendAsync(It.IsAny<FileChangeLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns((FileChangeLogEntry e, CancellationToken _) => { _changes.Add(e); return Task.CompletedTask; });

        _sut = new HomeDirectoryService(
            shares.Object, users.Object, metadata.Object, acls.Object,
            versions.Object, links.Object, changes.Object,
            NullLogger<HomeDirectoryService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private User AddUser(string username)
    {
        var user = new User(Guid.NewGuid(), username, username, "hash", "nthash");
        _users.Add(user);
        return user;
    }

    private UserContext Ctx(User user) => new(user, [], [], [], [_dept]);

    private Task<bool> Can(User user, string path, bool isDir, FilePermission permission)
        => _aclService.HasAccessAsync(Ctx(user), _shares.Single().Id, path, isDir, permission);

    [Fact]
    public async Task Owner_has_full_read_write_in_own_home_but_cannot_delete_or_re_permission_it()
    {
        var alice = AddUser("alice");
        Assert.Equal(HomeConfigureResult.Ok, await _sut.ConfigureAsync(_root, alice.Id));
        var home = HomeDirectoryService.HomePathOf(alice.Id);

        Assert.True(await Can(alice, "", true, FilePermission.ListReadData));            // SMB tree-connect
        Assert.True(await Can(alice, home, true, FilePermission.ListReadData));
        Assert.True(await Can(alice, home, true, FilePermission.CreateWriteData));
        Assert.True(await Can(alice, $"{home}/a/b.txt", false, FilePermission.Delete));
        Assert.False(await Can(alice, home, true, FilePermission.Delete));               // the home itself
        Assert.False(await Can(alice, "", true, FilePermission.CreateWriteData));        // nothing next to homes
        Assert.False(await Can(alice, $"{home}/a", true, FilePermission.ChangePermissions));
        var homeDir = Path.Combine(_root, HomeDirectoryService.ShareName, home);
        Assert.True(Directory.Exists(homeDir));
        // SMB users write through the shared storage group, whatever the creator's umask.
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.GroupWrite | UnixFileMode.SetGroup,
                File.GetUnixFileMode(homeDir) & (UnixFileMode.GroupWrite | UnixFileMode.SetGroup));
    }

    [Fact]
    public async Task Other_users_and_department_defaults_never_reach_a_home()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        var admin = AddUser("admin");
        admin.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, admin.Id);
        var aliceHome = HomeDirectoryService.HomePathOf(alice.Id);

        Assert.False(await Can(bob, aliceHome, true, FilePermission.ListReadData));
        Assert.False(await Can(bob, $"{aliceHome}/x.txt", false, FilePermission.ListReadData));
        Assert.False(await Can(admin, aliceHome, true, FilePermission.ListReadData));
        Assert.False(await Can(admin, "", true, FilePermission.ListReadData));           // no home → nothing
    }

    [Fact]
    public async Task Disabling_revokes_all_access_but_keeps_files_and_enabling_restores_it()
    {
        var alice = AddUser("alice");
        await _sut.ConfigureAsync(_root, alice.Id);
        var home = HomeDirectoryService.HomePathOf(alice.Id);
        var file = Path.Combine(_root, HomeDirectoryService.ShareName, home, "keep.txt");
        await File.WriteAllTextAsync(file, "data");

        await _sut.SetEnabledAsync(alice.Id, false);

        Assert.False(alice.HomeDirectoryEnabled);
        Assert.False(await Can(alice, "", true, FilePermission.ListReadData));
        Assert.False(await Can(alice, home, true, FilePermission.ListReadData));
        Assert.True(File.Exists(file));
        Assert.Null(await _sut.EnsureHomeAsync(alice));

        await _sut.SetEnabledAsync(alice.Id, true);

        Assert.True(await Can(alice, home, true, FilePermission.ListReadData));
        Assert.True(await Can(alice, $"{home}/keep.txt", false, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Global_switch_disables_every_home_and_keeps_per_user_settings()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        bob.HomeDirectoryEnabled = false;
        await _sut.ConfigureAsync(_root, alice.Id);

        await _sut.SetGloballyEnabledAsync(false);

        Assert.False(_shares.Single().IsEnabled);
        Assert.Null(await _sut.EnsureHomeAsync(alice));
        Assert.True(alice.HomeDirectoryEnabled);
        Assert.False(bob.HomeDirectoryEnabled);

        await _sut.SetGloballyEnabledAsync(true);

        Assert.NotNull(await _sut.EnsureHomeAsync(alice));
        Assert.Null(await _sut.EnsureHomeAsync(bob));
    }

    [Fact]
    public async Task Deleting_a_home_removes_content_links_and_access_and_recreates_it_empty()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        await _sut.ConfigureAsync(_root, alice.Id);
        var aliceHome = HomeDirectoryService.HomePathOf(alice.Id);
        var aliceDir = Path.Combine(_root, HomeDirectoryService.ShareName, aliceHome);
        Directory.CreateDirectory(Path.Combine(aliceDir, "docs"));
        await File.WriteAllTextAsync(Path.Combine(aliceDir, "docs", "a.txt"), "data");
        var aliceLink = new ShareLink { ShareId = _shares.Single().Id, RootRelativePath = $"{aliceHome}/docs" };
        var bobLink = new ShareLink { ShareId = _shares.Single().Id, RootRelativePath = HomeDirectoryService.HomePathOf(bob.Id) };
        _links.AddRange([aliceLink, bobLink]);

        Assert.True(await _sut.DeleteHomeAsync(alice.Id));

        Assert.False(File.Exists(Path.Combine(aliceDir, "docs", "a.txt")));
        Assert.Equal([bobLink], _links);
        Assert.Contains(aliceHome, _deletedVersionPaths);
        Assert.Contains(_changes, c => c.Path == aliceHome && c.ChangeType == FileChangeType.Deleted);
        // Alice still has an enabled home, so she gets a fresh empty one with her access restored.
        Assert.True(Directory.Exists(aliceDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(aliceDir));
        Assert.True(await Can(alice, $"{aliceHome}/new.txt", false, FilePermission.CreateWriteData));
        Assert.True(await Can(bob, HomeDirectoryService.HomePathOf(bob.Id), true, FilePermission.ListReadData));
    }

    [Fact]
    public async Task Deleting_an_orphaned_home_leaves_nothing_behind()
    {
        var admin = AddUser("admin");
        var ghost = AddUser("ghost");
        await _sut.ConfigureAsync(_root, admin.Id);
        _users.Remove(ghost);                                   // account deleted, folder left over

        Assert.True(await _sut.DeleteHomeAsync(ghost.Id));

        Assert.False(Directory.Exists(Path.Combine(_root, HomeDirectoryService.ShareName, HomeDirectoryService.HomePathOf(ghost.Id))));
        Assert.DoesNotContain(_acl, e => e.PrincipalId == ghost.Id);
    }

    [Fact]
    public async Task EnsureHome_is_idempotent()
    {
        var alice = AddUser("alice");
        await _sut.ConfigureAsync(_root, alice.Id);
        var entries = _acl.Count;

        await _sut.EnsureHomeAsync(alice);
        await _sut.BackfillAsync();

        Assert.Equal(entries, _acl.Count);
        Assert.Equal(3, _acl.Count(e => e.PrincipalId == alice.Id));
    }

    [Fact]
    public async Task Smb_share_list_restricts_the_home_share_to_users_with_an_enabled_home()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        bob.HomeDirectoryEnabled = false;
        var carol = AddUser("carol");
        carol.IsEnabled = false;
        await _sut.ConfigureAsync(_root, alice.Id);
        var other = new ShareDefinition("projects", Path.Combine(_root, "projects"));

        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync([_shares.Single(), other]);
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _users.ToList());
        var bridge = new Kaimo_File_Server.SmbBridge.Services.ShareGrpcService(
            shares.Object, users.Object,
            NullLogger<Kaimo_File_Server.SmbBridge.Services.ShareGrpcService>.Instance);

        var reply = await bridge.ListShares(new Kaimo_File_Server.SmbBridge.Grpc.ListSharesRequest(), null!);

        var home = reply.Shares.Single(s => s.Name == HomeDirectoryService.ShareName);
        Assert.True(home.Restricted);
        Assert.Equal(["alice"], home.AllowedUsers);
        var plain = reply.Shares.Single(s => s.Name == "projects");
        Assert.False(plain.Restricted);
        Assert.Empty(plain.AllowedUsers);
    }

    [Fact]
    public async Task Configure_refuses_an_existing_users_share_and_a_second_setup()
    {
        var admin = AddUser("admin");
        _shares.Add(new ShareDefinition(HomeDirectoryService.ShareName, Path.Combine(_root, "other")));
        Assert.Equal(HomeConfigureResult.NameTaken, await _sut.ConfigureAsync(_root, admin.Id));

        _shares.Clear();
        Assert.Equal(HomeConfigureResult.Ok, await _sut.ConfigureAsync(_root, admin.Id));
        Assert.Equal(HomeConfigureResult.AlreadyConfigured, await _sut.ConfigureAsync(_root, admin.Id));

        var share = Assert.Single(_shares);
        Assert.True(share.IsUserHomes);
        Assert.False(share.IsShareHidden);                   // listed on SMB/WebDAV
        Assert.True(share.IsRecycleEnabled);                 // per home: <userId>/.RECYCLE_BIN
        Assert.Equal(1, share.RecycleRootDepth);
    }

    [Fact]
    public async Task Backfill_turns_the_recycle_bin_on_and_it_inherits_only_the_owner()
    {
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        await _sut.ConfigureAsync(_root, alice.Id);
        _shares.Single().IsRecycleEnabled = false;          // setup from before per-home recycle bins

        await _sut.BackfillAsync();

        Assert.True(_shares.Single().IsRecycleEnabled);
        var bin = $"{HomeDirectoryService.HomePathOf(alice.Id)}/.RECYCLE_BIN";
        Assert.True(await Can(alice, bin, true, FilePermission.ListReadData));
        Assert.True(await Can(alice, $"{bin}/a.txt", false, FilePermission.Delete));
        Assert.False(await Can(bob, bin, true, FilePermission.ListReadData));
    }
}
