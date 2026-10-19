using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Web.Controllers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A database dump contains every password hash and protected credential. These
/// tests pin down who may obtain one: only a user who still holds
/// <see cref="ManagementPermission.ManageBackups"/>, once per issued link, never
/// on a public read-only demo.
/// </summary>
public sealed class BackupDownloadSecurityTests : IDisposable
{
    private const string BackupName = "kaimo_20260820-031542_manual.dump";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kaimo-backup-dl-" + Guid.NewGuid().ToString("N"));
    private readonly BackupDownloadTokenService _tokens =
        new(DataProtectionProvider.Create("KaimoBackupTests"));
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IUserContextFactory> _contexts = new();
    private readonly Mock<IManagementAuthService> _auth = new();
    private readonly Mock<IDatabaseBackupService> _backups = new();
    private readonly User _admin = new(Guid.NewGuid(), "Admin", "admin", "hash", "nt");

    public BackupDownloadSecurityTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, BackupName);
        File.WriteAllText(path, "dump");
        _backups.Setup(b => b.ResolveBackupPath(BackupName)).Returns(path);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(_admin);
        _contexts.Setup(c => c.CreateAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => new UserContext(u, [], [], new HashSet<string>()));
        GrantBackupPermission(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Token ──

    [Fact]
    public void Token_RoundTripsFileAndUser_AndIsSingleUse()
    {
        var token = _tokens.Protect(BackupName, _admin.Id);

        Assert.True(_tokens.TryConsume(token, out var fileName, out var userId));
        Assert.Equal(BackupName, fileName);
        Assert.Equal(_admin.Id, userId);
        Assert.False(_tokens.TryConsume(token, out _, out _));
    }

    [Fact]
    public void Token_EachIssueIsIndependent()
    {
        var first = _tokens.Protect(BackupName, _admin.Id);
        var second = _tokens.Protect(BackupName, _admin.Id);

        Assert.NotEqual(first, second);
        Assert.True(_tokens.TryConsume(first, out _, out _));
        Assert.True(_tokens.TryConsume(second, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Token_GarbageIsRejected(string token)
        => Assert.False(_tokens.TryConsume(token, out _, out _));

    [Fact]
    public void Token_FromOtherServiceInstanceKeyIsRejected()
    {
        var foreign = new BackupDownloadTokenService(DataProtectionProvider.Create("OtherApp"));
        Assert.False(_tokens.TryConsume(foreign.Protect(BackupName, _admin.Id), out _, out _));
    }

    // ── Controller ──

    [Fact]
    public async Task Download_ValidToken_StreamsFile()
    {
        var result = await Controller().Download(_tokens.Protect(BackupName, _admin.Id));

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(BackupName, file.FileDownloadName);
        await file.FileStream.DisposeAsync();
    }

    [Fact]
    public async Task Download_ReplayedToken_IsUnauthorized()
    {
        var token = _tokens.Protect(BackupName, _admin.Id);
        var first = Assert.IsType<FileStreamResult>(await Controller().Download(token));
        await first.FileStream.DisposeAsync();

        Assert.IsType<UnauthorizedResult>(await Controller().Download(token));
    }

    [Fact]
    public async Task Download_PermissionRevokedAfterIssue_IsForbidden()
    {
        var token = _tokens.Protect(BackupName, _admin.Id);
        GrantBackupPermission(false);

        var result = Assert.IsType<StatusCodeResult>(await Controller().Download(token));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task Download_UserDisabledAfterIssue_IsUnauthorized()
    {
        var token = _tokens.Protect(BackupName, _admin.Id);
        _users.Setup(u => u.GetByIdAsync(_admin.Id)).ReturnsAsync(
            new User(_admin.Id, "Admin", "admin", "hash", "nt", isEnabled: false));

        Assert.IsType<UnauthorizedResult>(await Controller().Download(token));
    }

    [Fact]
    public async Task Download_InDemoMode_IsForbiddenEvenWithValidToken()
    {
        var result = await Controller(demo: true).Download(_tokens.Protect(BackupName, _admin.Id));

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        _backups.Verify(b => b.ResolveBackupPath(It.IsAny<string>()), Times.Never);
    }

    // ── Backup creation ──

    [Fact]
    public async Task ManualBackup_InDemoMode_IsRejected_ButScheduledStillRuns()
    {
        var runner = new DumpWritingRunner();
        var service = BackupService(runner, demo: true);

        await Assert.ThrowsAsync<ReadOnlyDemoException>(
            () => service.CreateBackupAsync(BackupTrigger.Manual));
        Assert.Equal(0, runner.Calls);

        await service.CreateBackupAsync(BackupTrigger.Scheduled);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task CreatedBackup_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only; the production container is Linux.

        var created = await BackupService(new DumpWritingRunner(), demo: false)
            .CreateBackupAsync(BackupTrigger.Manual);

        var mode = File.GetUnixFileMode(Path.Combine(_dir, created.FileName));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    private void GrantBackupPermission(bool granted)
        => _auth.Setup(a => a.GetEffectivePermissionsAtAsync(
                It.IsAny<UserContext>(), ScopeType.Global, Guid.Empty))
            .ReturnsAsync(granted ? ManagementPermission.ManageBackups : ManagementPermission.None);

    private BackupDownloadController Controller(bool demo = false)
        => new(_tokens, _backups.Object, _users.Object, _contexts.Object, _auth.Object,
            new DemoModeOptions { ReadOnly = demo })
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private DatabaseBackupService BackupService(IProcessRunner runner, bool demo)
        => new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:RootPath"] = _dir,
                ["ConnectionStrings:Default"] = "Host=db;Database=kaimo;Username=u;Password=p",
            }).Build(),
            runner,
            TimeProvider.System,
            NullLogger<DatabaseBackupService>.Instance,
            new DemoModeOptions { ReadOnly = demo });

    /// <summary>Stands in for pg_dump: writes the file named after <c>--file</c>.</summary>
    private sealed class DumpWritingRunner : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var target = arguments[arguments.ToList().IndexOf("--file") + 1];
            File.WriteAllText(target, "dump");
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
