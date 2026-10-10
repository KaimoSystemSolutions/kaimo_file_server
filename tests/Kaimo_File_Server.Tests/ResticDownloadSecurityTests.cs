using System.Text;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The recovery kit holds the repository password: whoever has it can read every backup.
/// These tests pin down who may download it (Global ManageBackupRepositories, once per
/// issued link, never in the demo) and what it contains (password and kit code, but never
/// the storage backend's own credentials).
/// </summary>
public sealed class ResticDownloadSecurityTests : DatabaseTestBase
{
    private const string RepoPassword = "repo-password-0123456789";
    private const string RestUser = "kaimo-rest-user";
    private const string RestSecret = "rest-secret-value";
    private const string S3Secret = "s3-secret-access-key";

    private readonly IDataProtectionProvider _protection = DataProtectionProvider.Create("KaimoResticTests");
    private readonly BackupDownloadTokenService _tokens;
    private readonly ICredentialVault _vault;
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "hash", "nt");
    private readonly IConfiguration _config = new ConfigurationBuilder().Build();

    public ResticDownloadSecurityTests()
    {
        _tokens = new BackupDownloadTokenService(_protection);
        _vault = new DataProtectionCredentialVault(_protection);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(_admin);
        _contexts.Setup(c => c.CreateAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => new UserContext(u, [], [], new HashSet<string>()));
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        _auth.Setup(a => a.GetAuthorizedShareIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([]));
        GrantRepositoryPermission(true);
    }

    private void GrantRepositoryPermission(bool granted)
        => _auth.Setup(a => a.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupRepositories))
            .ReturnsAsync(granted);

    private ResticTargetResolver Resolver => new(DbFactory, _vault, _config);

    private BackupCatalogService Catalog(bool demo = false) => new(
        DbFactory,
        _auth.Object,
        Resolver,
        new ResticClient(new VersionRunner(), _config),
        Mock.Of<IBackupRunner>(),
        new DemoModeOptions { ReadOnly = demo },
        TimeProvider.System,
        NullLogger<BackupCatalogService>.Instance);

    private ResticDownloadController Controller(bool demo = false)
        => new(_tokens, Catalog(demo), _users.Object, _contexts.Object, _auth.Object,
            new DemoModeOptions { ReadOnly = demo }, NullLogger<ResticDownloadController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    /// <summary>A REST repository with stored backend credentials, as created through the UI.</summary>
    private async Task<BackupRepository> SeedRepositoryAsync(BackupBackend backend = BackupBackend.Rest)
    {
        var repo = new BackupRepository
        {
            Name = "Offsite NAS",
            Backend = backend,
            ResticRepositoryId = "5f1d0c",
            SettingsJson = backend == BackupBackend.S3
                ? new ResticRepositorySettings { Endpoint = "https://s3.example.com", Bucket = "kaimo", Region = "eu-central-1" }.Serialize()
                : new ResticRepositorySettings { Endpoint = "https://backup.example.com:8000", Path = "kaimo" }.Serialize(),
        };
        repo.EncryptedPassword = Resolver.ProtectPassword(repo.Id, RepoPassword);
        repo.EncryptedSecrets = Resolver.ProtectSecrets(repo.Id, backend == BackupBackend.S3
            ? new ResticBackendSecrets { S3AccessKeyId = "AKIAEXAMPLE", S3SecretAccessKey = S3Secret }
            : new ResticBackendSecrets { RestUsername = RestUser, RestPassword = RestSecret });
        await using var db = NewContext();
        db.BackupRepositories.Add(repo);
        await db.SaveChangesAsync();
        return repo;
    }

    private string KitToken(Guid repositoryId) => _tokens.Protect(ResticDownloadController.KitSubjectPrefix + repositoryId, _admin.Id);

    // ── Controller ──

    [Fact]
    public async Task Kit_ValidToken_ReturnsUncachedTextFile()
    {
        var repo = await SeedRepositoryAsync();
        var controller = Controller();

        var file = Assert.IsType<FileContentResult>(await controller.RecoveryKit(KitToken(repo.Id), CancellationToken.None));

        Assert.Equal("kaimo-recovery-kit-Offsite-NAS.txt", file.FileDownloadName);
        Assert.Contains(RepoPassword, Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Kit_ReplayedToken_IsUnauthorized()
    {
        var repo = await SeedRepositoryAsync();
        var token = KitToken(repo.Id);
        Assert.IsType<FileContentResult>(await Controller().RecoveryKit(token, CancellationToken.None));

        Assert.IsType<UnauthorizedResult>(await Controller().RecoveryKit(token, CancellationToken.None));
    }

    [Fact]
    public async Task Kit_PermissionRevokedAfterIssue_IsForbidden()
    {
        var repo = await SeedRepositoryAsync();
        var token = KitToken(repo.Id);
        GrantRepositoryPermission(false);

        var result = Assert.IsType<StatusCodeResult>(await Controller().RecoveryKit(token, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task Kit_ScopedBackupAdminWithoutGlobalRepositoryRight_IsForbidden()
    {
        var repo = await SeedRepositoryAsync();
        GrantRepositoryPermission(false);
        _auth.Setup(a => a.GetAuthorizedShareIdsAsync(It.IsAny<UserContext>(), ManagementPermission.ManageBackupJobs))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo([Guid.NewGuid()]));

        var result = Assert.IsType<StatusCodeResult>(await Controller().RecoveryKit(KitToken(repo.Id), CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task Kit_UserDisabledAfterIssue_IsUnauthorized()
    {
        var repo = await SeedRepositoryAsync();
        var token = KitToken(repo.Id);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(new User(_admin.Id, "Admin", "admin", "hash", "nt", isEnabled: false));

        Assert.IsType<UnauthorizedResult>(await Controller().RecoveryKit(token, CancellationToken.None));
    }

    [Fact]
    public async Task Kit_InDemoMode_IsForbiddenEvenWithValidToken()
    {
        var repo = await SeedRepositoryAsync();

        var result = Assert.IsType<StatusCodeResult>(await Controller(demo: true).RecoveryKit(KitToken(repo.Id), CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        await using var db = NewContext();
        Assert.Null((await db.BackupRepositories.SingleAsync()).KitDownloadedAtUtc);
    }

    [Fact]
    public async Task Kit_TokenIssuedForADatabaseDump_IsUnauthorized()
    {
        await SeedRepositoryAsync();
        var dumpToken = _tokens.Protect("kaimo_20260820-031542_manual.dump", _admin.Id);

        Assert.IsType<UnauthorizedResult>(await Controller().RecoveryKit(dumpToken, CancellationToken.None));
    }

    [Fact]
    public async Task Kit_UnknownRepository_IsNotFound()
        => Assert.IsType<NotFoundResult>(await Controller().RecoveryKit(KitToken(Guid.NewGuid()), CancellationToken.None));

    // ── Kit content & confirmation ──

    [Theory]
    [InlineData(BackupBackend.Rest)]
    [InlineData(BackupBackend.S3)]
    public async Task Kit_ContainsPasswordAndCode_ButNeverBackendCredentials(BackupBackend backend)
    {
        var repo = await SeedRepositoryAsync(backend);
        var admin = new UserContext(_admin, [], [], new HashSet<string>());

        var (_, content) = await Catalog().BuildRecoveryKitAsync(admin, repo.Id);

        Assert.Contains("RESTIC_PASSWORD=" + RepoPassword, content);
        Assert.Contains(ResticBackend.KitCode(RepoPassword), content);
        Assert.Contains("5f1d0c", content);
        Assert.Contains("restic 0.19.1", content);
        Assert.DoesNotContain(RestSecret, content);
        Assert.DoesNotContain(RestUser, content);
        Assert.DoesNotContain(S3Secret, content);
        Assert.DoesNotContain("AKIAEXAMPLE", content);
        await using var db = NewContext();
        Assert.NotNull((await db.BackupRepositories.SingleAsync()).KitDownloadedAtUtc);
    }

    [Fact]
    public async Task ConfirmKit_RequiresDownload_RejectsWrongCode_ThenActivates()
    {
        var repo = await SeedRepositoryAsync();
        var admin = new UserContext(_admin, [], [], new HashSet<string>());
        var catalog = Catalog();
        var code = ResticBackend.KitCode(RepoPassword);

        Assert.Equal("kit_not_downloaded",
            (await Assert.ThrowsAsync<ResticException>(() => catalog.ConfirmRecoveryKitAsync(admin, repo.Id, code))).Code);

        await catalog.BuildRecoveryKitAsync(admin, repo.Id);
        Assert.False(await catalog.ConfirmRecoveryKitAsync(admin, repo.Id, code == "AAAAAA" ? "BBBBBB" : "AAAAAA"));
        await using (var db = NewContext())
            Assert.Equal(BackupRepositoryState.PendingRecoveryKit, (await db.BackupRepositories.SingleAsync()).State);

        Assert.True(await catalog.ConfirmRecoveryKitAsync(admin, repo.Id, code));
        await using var check = NewContext();
        var saved = await check.BackupRepositories.SingleAsync();
        Assert.Equal(BackupRepositoryState.Active, saved.State);
        Assert.Equal(_admin.Id, saved.KitConfirmedByUserId);
    }

    /// <summary>Answers only <c>restic version</c>; the kit never needs another restic call.</summary>
    private sealed class VersionRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, Action<string>? onStdoutLine, bool clearEnvironment,
            CancellationToken cancellationToken = default)
            => arguments is ["version"]
                ? Task.FromResult(new ProcessResult(0, "restic 0.19.1 compiled with go1.24 on linux/amd64\n", ""))
                : throw new InvalidOperationException("Unexpected restic call: " + string.Join(' ', arguments));
    }
}
