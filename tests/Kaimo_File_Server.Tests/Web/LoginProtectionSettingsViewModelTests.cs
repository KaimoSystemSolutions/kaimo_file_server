using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Settings → Login protection: loads the brute-force thresholds (defaults when unset),
/// validates every field against the bounds of <see cref="LoginProtectionSettings"/> and
/// writes all keys at once. Gated by the global ManageSystemSettings permission, re-checked
/// on save.
/// </summary>
public sealed class LoginProtectionSettingsViewModelTests
{
    private readonly User _actor = new(Guid.NewGuid(), "Admin", "admin", "pw", "nt");
    private readonly Mock<IConfigRepository> _config = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly Dictionary<string, object> _stored = new();
    private Dictionary<string, object>? _written;

    public LoginProtectionSettingsViewModelTests()
    {
        _config.Setup(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((string key, int fallback) => _stored.TryGetValue(key, out var v) ? (int)v : fallback);
        _config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string key, bool fallback) => _stored.TryGetValue(key, out var v) ? (bool)v : fallback);
        _config.Setup(c => c.SetManyAsync(It.IsAny<Dictionary<string, object>>()))
            .Callback((Dictionary<string, object> values) => _written = values)
            .Returns(Task.CompletedTask);
    }

    private LoginProtectionSettingsViewModel Create(bool canManage = true)
    {
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var actorContext = new UserContext(_actor, [], [], []);
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(actorContext);
        SetPermission(canManage);

        return new LoginProtectionSettingsViewModel(authState.Object, contexts.Object, _mgmtAuth.Object,
            _config.Object, NullLogger<LoginProtectionSettingsViewModel>.Instance);
    }

    private void SetPermission(bool canManage)
        => _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ManageSystemSettings))
            .ReturnsAsync(canManage);

    [Fact]
    public async Task Load_WithoutStoredKeys_ShowsTheDefaults()
    {
        var vm = Create();
        await vm.LoadAsync();

        Assert.True(vm.CanManage);
        Assert.Equal(5, vm.MaxAttempts);
        Assert.Equal(15, vm.LockoutMinutes);
        Assert.Equal(10, vm.AccountWideFactor);
        Assert.Equal(15, vm.IpMaxAttempts);
        Assert.Equal(30, vm.IpLockoutMinutes);
        Assert.Equal(5, vm.ShareLinkMaxAttemptsPerAddress);
        Assert.Equal(50, vm.ShareLinkMaxAttemptsPerLink);
        Assert.Equal(30, vm.ShareLinkLockoutMinutes);
        Assert.True(vm.SmbLockoutEnabled);
        Assert.Equal(10, vm.SmbLockoutAttempts);
        Assert.Equal(15, vm.SmbLockoutMinutes);
    }

    [Fact]
    public async Task Load_ShowsTheStoredValues()
    {
        _stored[LoginProtectionSettings.MaxAttemptsKey] = 3;
        _stored[LoginProtectionSettings.LockoutMinutesKey] = 20;
        _stored[LoginProtectionSettings.AccountWideFactorKey] = 4;
        _stored[LoginProtectionSettings.IpMaxAttemptsKey] = 25;
        _stored[LoginProtectionSettings.IpLockoutMinutesKey] = 60;
        _stored[LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey] = 7;
        _stored[LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey] = 70;
        _stored[LoginProtectionSettings.ShareLinkLockoutMinutesKey] = 45;
        _stored[LoginProtectionSettings.SmbLockoutEnabledKey] = false;
        _stored[LoginProtectionSettings.SmbLockoutAttemptsKey] = 12;
        _stored[LoginProtectionSettings.SmbLockoutMinutesKey] = 25;

        var vm = Create();
        await vm.LoadAsync();

        Assert.Equal(3, vm.MaxAttempts);
        Assert.Equal(20, vm.LockoutMinutes);
        Assert.Equal(4, vm.AccountWideFactor);
        Assert.Equal(25, vm.IpMaxAttempts);
        Assert.Equal(60, vm.IpLockoutMinutes);
        Assert.Equal(7, vm.ShareLinkMaxAttemptsPerAddress);
        Assert.Equal(70, vm.ShareLinkMaxAttemptsPerLink);
        Assert.Equal(45, vm.ShareLinkLockoutMinutes);
        Assert.False(vm.SmbLockoutEnabled);
        Assert.Equal(12, vm.SmbLockoutAttempts);
        Assert.Equal(25, vm.SmbLockoutMinutes);
    }

    [Fact]
    public async Task Load_WithoutPermission_ReadsNothing()
    {
        var vm = Create(canManage: false);
        await vm.LoadAsync();

        Assert.False(vm.CanManage);
        _config.Verify(c => c.GetIntAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Save_WritesEveryKeyInOneBatch()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 4;
        vm.LockoutMinutes = 20;
        vm.AccountWideFactor = 8;
        vm.IpMaxAttempts = 30;
        vm.IpLockoutMinutes = 45;
        vm.ShareLinkMaxAttemptsPerAddress = 6;
        vm.ShareLinkMaxAttemptsPerLink = 60;
        vm.ShareLinkLockoutMinutes = 40;
        vm.SmbLockoutEnabled = false;
        vm.SmbLockoutAttempts = 9;
        vm.SmbLockoutMinutes = 10;

        await vm.SaveAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.SuccessMessage);
        Assert.NotNull(_written);
        Assert.Equal(new Dictionary<string, object>
        {
            [LoginProtectionSettings.MaxAttemptsKey] = 4,
            [LoginProtectionSettings.LockoutMinutesKey] = 20,
            [LoginProtectionSettings.AccountWideFactorKey] = 8,
            [LoginProtectionSettings.IpMaxAttemptsKey] = 30,
            [LoginProtectionSettings.IpLockoutMinutesKey] = 45,
            [LoginProtectionSettings.ShareLinkMaxAttemptsPerAddressKey] = 6,
            [LoginProtectionSettings.ShareLinkMaxAttemptsPerLinkKey] = 60,
            [LoginProtectionSettings.ShareLinkLockoutMinutesKey] = 40,
            [LoginProtectionSettings.SmbLockoutEnabledKey] = false,
            [LoginProtectionSettings.SmbLockoutAttemptsKey] = 9,
            [LoginProtectionSettings.SmbLockoutMinutesKey] = 10,
        }, _written);
    }

    [Theory]
    [InlineData(nameof(LoginProtectionSettingsViewModel.MaxAttempts), 0)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.MaxAttempts), 1001)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.LockoutMinutes), -5)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.LockoutMinutes), 10081)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.AccountWideFactor), 0)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.AccountWideFactor), 101)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.IpMaxAttempts), -1)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.IpMaxAttempts), 1001)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.IpLockoutMinutes), 0)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.ShareLinkMaxAttemptsPerAddress), 0)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.ShareLinkMaxAttemptsPerLink), 1001)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.ShareLinkLockoutMinutes), 10081)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.SmbLockoutAttempts), 0)]
    [InlineData(nameof(LoginProtectionSettingsViewModel.SmbLockoutMinutes), -1)]
    public async Task Save_RejectsOutOfRangeValues_AsFieldError_AndWritesNothing(string field, int value)
    {
        var vm = Create();
        await vm.LoadAsync();
        typeof(LoginProtectionSettingsViewModel).GetProperty(field)!.SetValue(vm, value);

        await vm.SaveAsync();

        Assert.NotNull(vm.ErrorFor(field));
        Assert.Single(vm.FieldErrors);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(_written);
    }

    [Fact]
    public async Task Save_AcceptsTheBoundaryValues()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 1;
        vm.LockoutMinutes = LoginProtectionSettings.MaxLockoutMinutes;
        vm.AccountWideFactor = LoginProtectionSettings.MaxAccountWideFactor;
        vm.IpMaxAttempts = LoginProtectionSettings.MaxAttemptsLimit;

        await vm.SaveAsync();

        Assert.Empty(vm.FieldErrors);
        Assert.NotNull(_written);
    }

    [Fact]
    public async Task Save_AfterCorrectingAnInvalidValue_ClearsTheFieldError()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 0;
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorFor(nameof(vm.MaxAttempts)));

        vm.MaxAttempts = 6;
        await vm.SaveAsync();

        Assert.Null(vm.ErrorFor(nameof(vm.MaxAttempts)));
        Assert.Empty(vm.FieldErrors);
        Assert.NotNull(_written);
    }

    [Fact]
    public async Task CorrectingAnInvalidValue_ClearsItsFieldError_BeforeTheNextSave()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 0;
        vm.LockoutMinutes = 0;
        await vm.SaveAsync();

        vm.MaxAttempts = 6;

        Assert.Null(vm.ErrorFor(nameof(vm.MaxAttempts)));
        Assert.NotNull(vm.ErrorFor(nameof(vm.LockoutMinutes)));
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task ChangingAnInvalidValue_ToAnotherInvalidValue_KeepsTheFieldError()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 0;
        await vm.SaveAsync();

        vm.MaxAttempts = 5000;

        Assert.NotNull(vm.ErrorFor(nameof(vm.MaxAttempts)));
    }

    [Fact]
    public async Task CorrectingTheLastInvalidValue_ClearsTheErrorBanner()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.SmbLockoutMinutes = 0;
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorMessage);

        vm.SmbLockoutMinutes = 20;

        Assert.Empty(vm.FieldErrors);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task InvalidValue_BeforeTheFirstSave_RaisesNoFieldError()
    {
        var vm = Create();
        await vm.LoadAsync();

        vm.MaxAttempts = 0;

        Assert.Empty(vm.FieldErrors);
    }

    [Fact]
    public async Task Save_WithTheSmbLockoutDisabled_IgnoresInvalidSmbValues_AndKeepsTheirStoredValues()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.SmbLockoutEnabled = false;
        vm.SmbLockoutAttempts = 0;
        vm.SmbLockoutMinutes = 99999;

        await vm.SaveAsync();

        Assert.Empty(vm.FieldErrors);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(_written);
        Assert.Equal(false, _written[LoginProtectionSettings.SmbLockoutEnabledKey]);
        Assert.False(_written.ContainsKey(LoginProtectionSettings.SmbLockoutAttemptsKey));
        Assert.False(_written.ContainsKey(LoginProtectionSettings.SmbLockoutMinutesKey));
        Assert.Equal(vm.MaxAttempts, _written[LoginProtectionSettings.MaxAttemptsKey]);
    }

    [Fact]
    public async Task Save_WithTheSmbLockoutDisabled_StillWritesValidSmbValues()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.SmbLockoutEnabled = false;
        vm.SmbLockoutAttempts = 8;

        await vm.SaveAsync();

        Assert.NotNull(_written);
        Assert.Equal(8, _written[LoginProtectionSettings.SmbLockoutAttemptsKey]);
        Assert.Equal(vm.SmbLockoutMinutes, _written[LoginProtectionSettings.SmbLockoutMinutesKey]);
    }

    [Fact]
    public async Task DisablingTheSmbLockout_ClearsTheSmbFieldErrors()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.SmbLockoutAttempts = 0;
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorFor(nameof(vm.SmbLockoutAttempts)));

        vm.SmbLockoutEnabled = false;

        Assert.Empty(vm.FieldErrors);
        Assert.Null(vm.ErrorMessage);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 0)]
    [InlineData(1001, 10)]
    [InlineData(5, 101)]
    public async Task AccountWideAttempts_IsUnknown_WhileAnInputIsOutOfRange(int maxAttempts, int factor)
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = maxAttempts;
        vm.AccountWideFactor = factor;

        Assert.Null(vm.AccountWideAttempts);
    }

    [Fact]
    public async Task AccountWideAttempts_IsTheProduct_OfValidInputs()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.MaxAttempts = 4;
        vm.AccountWideFactor = 7;

        Assert.Equal(28, vm.AccountWideAttempts);
    }

    [Fact]
    public async Task Save_AfterThePermissionWasRevoked_WritesNothing()
    {
        var vm = Create();
        await vm.LoadAsync();
        SetPermission(false);

        await vm.SaveAsync();

        Assert.NotNull(vm.ErrorMessage);
        _config.Verify(c => c.SetManyAsync(It.IsAny<Dictionary<string, object>>()), Times.Never);
    }

    [Fact]
    public async Task Save_WithoutPermissionFromTheStart_WritesNothing()
    {
        var vm = Create(canManage: false);
        await vm.LoadAsync();

        await vm.SaveAsync();

        Assert.NotNull(vm.ErrorMessage);
        _config.Verify(c => c.SetManyAsync(It.IsAny<Dictionary<string, object>>()), Times.Never);
    }

    [Fact]
    public async Task Save_InDemoMode_ShowsTheReadOnlyNotice()
    {
        _config.Setup(c => c.SetManyAsync(It.IsAny<Dictionary<string, object>>()))
            .ThrowsAsync(new ReadOnlyDemoException());
        var vm = Create();
        await vm.LoadAsync();

        await vm.SaveAsync();

        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString("Web_Demo_ReadOnlyNotice"), vm.ErrorMessage);
        Assert.Null(vm.SuccessMessage);
    }
}
