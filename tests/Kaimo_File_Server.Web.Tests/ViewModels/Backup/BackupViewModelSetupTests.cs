using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Credentials;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Core.Services.Versioning;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Web.Components.ViewModels.Backup;
using Kaimo_File_Server.Web.Services.Downloads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Backup;

/// <summary>
/// Setting up a repository: results and failures of "Test location" and "Connect" belong to
/// the setup section next to the buttons. The page banner only reports a repository that was
/// actually added. A failed "connect existing" test suggests repository folders found below
/// the local roots.
/// </summary>
public sealed class BackupViewModelSetupTests : DatabaseTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-setup-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly Mock<IProcessRunner> _process = new();
    private readonly User _user;

    /// <summary>Exit code and stderr of <c>restic cat config</c>.</summary>
    private (int Code, string Stderr) _cat = (10, "");

    /// <summary>When set, every permission check after <c>restic cat config</c> ran fails unexpectedly (e.g. database outage).</summary>
    private bool _failAfterCat;
    private bool _catRan;

    public BackupViewModelSetupTests()
    {
        Directory.CreateDirectory(_root);
        _user = SeedUser("alice");
        _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .Returns(() => _failAfterCat && _catRan ? throw new InvalidOperationException("database down") : Task.FromResult(true));
        _auth.Setup(a => a.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>())).ReturnsAsync(true);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        _process.Setup(p => p.RunStreamingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? _, Action<string>? onLine, bool _, CancellationToken _) =>
            {
                if (!args.Contains("cat"))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                _catRan = true;
                if (_cat.Code == 0)
                    onLine?.Invoke("{\"version\":2,\"id\":\"repo-1\"}");
                return Task.FromResult(new ProcessResult(_cat.Code, "", _cat.Stderr));
            });
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private async Task<BackupViewModel> NewFormAsync(bool connectExisting, string folder = "typo")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Restic:LocalRoots"] = _root,
            ["Backup:Restic:CacheDirectory"] = Path.Combine(_root, ".cache"),
            ["Backup:Restic:TempDirectory"] = Path.Combine(_root, ".tmp"),
        }).Build();
        var resolver = new ResticTargetResolver(DbFactory, Mock.Of<ICredentialVault>(), config);
        var restic = new ResticClient(_process.Object, config);
        var catalog = new BackupCatalogService(DbFactory, _auth.Object, resolver, restic, Mock.Of<IBackupRunner>(),
            new DemoModeOptions(), TimeProvider.System, NullLogger<BackupCatalogService>.Instance);
        var executor = new BackupRestoreExecutor(DbFactory, resolver, restic, Mock.Of<IFileVersionService>(),
            Mock.Of<IFileChangeLog>(), new ShareLockManager(), config, TimeProvider.System, NullLogger<BackupRestoreExecutor>.Instance);
        var vm = new BackupViewModel(AuthStateFor("alice"), UserContextFactoryFor(("alice", ContextFor(_user))).Object, catalog,
            Mock.Of<IBackupRunner>(), Mock.Of<IDepartmentRepository>(), new BackupDownloadTokenService(new EphemeralDataProtectionProvider()),
            executor, new DemoModeOptions(), TimeProvider.System, NullLogger<BackupViewModel>.Instance,
            Mock.Of<IUserRepository>());
        await vm.LoadAsync();
        vm.NewRepository();
        vm.RepositoryForm.Name = "Old NAS";
        vm.RepositoryForm.Backend = BackupBackend.Local;
        vm.RepositoryForm.Path = Path.Combine(_root, folder);
        vm.SetConnectExisting(connectExisting);
        return vm;
    }

    private string RepositoryFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "keys"));
        File.WriteAllText(Path.Combine(path, "config"), "");
        return path;
    }

    [Fact]
    public async Task ConnectTest_FindsNothing_SuggestsRepositoryFolders_WithoutAPageBanner()
    {
        var oldNas = RepositoryFolder("old-nas");
        var vm = await NewFormAsync(connectExisting: true);

        await vm.ProbeAsync();

        Assert.Equal(BackupRepositorySetupStep.NothingToConnect, vm.RepositorySetupStep);
        Assert.Equal([oldNas], vm.LocalRepositoryHints);
        Assert.Null(vm.SuccessMessage);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.SetupError);
    }

    [Fact]
    public async Task NewTest_FindsAnEmptyLocation_ReportsItInline_NotAsPageBanner()
    {
        RepositoryFolder("old-nas");
        var vm = await NewFormAsync(connectExisting: false);

        await vm.ProbeAsync();

        Assert.Equal(BackupRepositorySetupStep.Initialize, vm.RepositorySetupStep);
        Assert.Empty(vm.LocalRepositoryHints);
        Assert.Null(vm.SuccessMessage);
    }

    [Fact]
    public async Task FailedTest_ShowsTheErrorInline_NotAsPageBanner()
    {
        _cat = (1, "Fatal: permission denied");
        var vm = await NewFormAsync(connectExisting: true);

        await vm.ProbeAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(BackupViewModel.ErrorText("permission_denied"), vm.SetupError);
        Assert.Equal(BackupRepositorySetupStep.Test, vm.RepositorySetupStep);
    }

    [Fact]
    public async Task ConnectWithAWrongPassword_ShowsTheErrorInline_NotAsPageBanner()
    {
        _cat = (12, "");
        var vm = await NewFormAsync(connectExisting: true, folder: "old-nas");
        await vm.ProbeAsync();
        Assert.Equal(BackupRepositorySetupStep.Connect, vm.RepositorySetupStep);
        vm.RepositoryForm.ExistingPassword = "not-it";

        Assert.False(await vm.CreateRepositoryAsync(connectExisting: true));

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(BackupViewModel.ErrorText("wrong_password"), vm.SetupError);
    }

    [Fact]
    public async Task ChangingTheLocation_ClearsTheInlineResult()
    {
        RepositoryFolder("old-nas");
        _cat = (1, "Fatal: permission denied");
        var vm = await NewFormAsync(connectExisting: true);
        await vm.ProbeAsync();
        Assert.NotNull(vm.SetupError);

        vm.ResetProbe();

        Assert.Null(vm.SetupError);
        Assert.Empty(vm.LocalRepositoryHints);
    }

    [Fact]
    public async Task HintLookupFails_TheProbeResultStillCounts_WithoutAnError()
    {
        RepositoryFolder("old-nas");
        var vm = await NewFormAsync(connectExisting: true);
        _failAfterCat = true;

        await vm.ProbeAsync();

        Assert.Equal(BackupRepositorySetupStep.NothingToConnect, vm.RepositorySetupStep);
        Assert.Empty(vm.LocalRepositoryHints);
        Assert.Null(vm.SetupError);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task FailureAfterTheRepositoryWasAdded_StaysVisibleAsPageBanner()
    {
        _cat = (0, "");
        var vm = await NewFormAsync(connectExisting: true, folder: "old-nas");
        await vm.ProbeAsync();
        vm.RepositoryForm.ExistingPassword = "secret";
        _failAfterCat = true;
        _catRan = false; // only the reload after connecting fails, not the permission check before it

        Assert.False(await vm.CreateRepositoryAsync(connectExisting: true));

        // The repository exists; the setup section is gone, so the error must not hide there.
        Assert.NotNull(vm.RepositoryForm.Id);
        Assert.Null(vm.SetupError);
        Assert.Equal(BackupViewModel.ErrorText("unexpected_error"), vm.ErrorMessage);
    }

    [Fact]
    public async Task FreeLocalPathField_ShowsAndAcceptsBackups_ButStoresTheContainerPath()
    {
        var vm = await NewFormAsync(connectExisting: true);

        // Default Backup:RootPath is /data/kaimo-backups.
        vm.LocalPathDisplay = "/backups/restic/old-nas";

        Assert.Equal("/data/kaimo-backups/restic/old-nas", vm.RepositoryForm.Path);
        Assert.Equal("/backups/restic/old-nas", vm.LocalPathDisplay);
    }

    [Fact]
    public async Task ChoosingASuggestedFolder_SetsTheLocationAndClearsTheResult()
    {
        var oldNas = RepositoryFolder("old-nas");
        var vm = await NewFormAsync(connectExisting: true);
        await vm.ProbeAsync();

        vm.UseLocalRepositoryHint(oldNas);

        Assert.Equal(oldNas, vm.RepositoryForm.Path);
        Assert.Equal(BackupRepositorySetupStep.Test, vm.RepositorySetupStep);
        Assert.Empty(vm.LocalRepositoryHints);
    }
}
