using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Settings → two-factor sign-in: permission gate, persisting the policy, the guard against
/// requiring a second factor from the saving administrator who has none, and the preview counts.
/// </summary>
public class SecondFactorSettingsViewModelTests : DatabaseTestBase
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IManagementAuthService> _mgmt = new();
    private readonly ConfigRepository _config;

    public SecondFactorSettingsViewModelTests()
        => _config = new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions()));

    private SecondFactorPolicyService Policy() => new(
        _config, new UserContextFactory(DbFactory), DepartmentRepo(), ScopedRoleRepo(), RoleRepo(),
        new SecondFactorRepository(DbFactory), new MemoryCache(new MemoryCacheOptions()));

    private SecondFactorSettingsViewModel Vm(User actor) => new(
        AuthStateFor(actor.Username), new UserContextFactory(DbFactory), _mgmt.Object, Policy(),
        new SecondFactorRepository(DbFactory), UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(),
        NullLogger<SecondFactorSettingsViewModel>.Instance);

    private User Admin()
    {
        var admin = SeedUser("admin");
        _mgmt.Setup(m => m.HasGlobalPermissionAsync(It.Is<UserContext>(c => c.User.Id == admin.Id), ManagementPermission.ManageSystemSettings))
            .ReturnsAsync(true);
        return admin;
    }

    [Fact]
    public async Task WithoutPermission_NothingLoadsOrSaves()
    {
        var user = SeedUser("erika");
        var vm = Vm(user);
        await vm.LoadAsync();

        Assert.False(vm.CanManage);
        vm.Mode = SecondFactorMode.Off;
        await vm.SaveAsync();
        Assert.Equal(SecondFactorMode.Optional, (await Policy().GetPolicyAsync()).Mode);
    }

    [Fact]
    public async Task Save_PersistsThePolicy()
    {
        var admin = Admin();
        await new SecondFactorRepository(DbFactory).ConfirmAsync(admin.Id, "secret", 1, [], Now);
        var group = Guid.NewGuid();
        var vm = Vm(admin);
        await vm.LoadAsync();

        vm.Mode = SecondFactorMode.Required;
        vm.GroupIds = [group];
        vm.Issuer = "Acme Files";
        await vm.SaveAsync();

        Assert.Null(vm.ErrorMessage);
        var saved = await Policy().GetPolicyAsync();
        Assert.Equal(SecondFactorMode.Required, saved.Mode);
        Assert.Contains(group, saved.GroupIds);
        Assert.Equal("Acme Files", saved.Issuer);
    }

    [Fact]
    public async Task Save_IsRefused_WhenItWouldRequireASecondFactorFromTheAdminWithoutOne()
    {
        var admin = Admin();
        var vm = Vm(admin);
        await vm.LoadAsync();

        vm.Mode = SecondFactorMode.Required;
        vm.AppliesToAll = true;
        await vm.SaveAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(SecondFactorMode.Optional, (await Policy().GetPolicyAsync()).Mode);
    }

    [Fact]
    public async Task Preview_CountsCoveredAndNotEnrolledUsers()
    {
        var admin = Admin();
        await new SecondFactorRepository(DbFactory).ConfirmAsync(admin.Id, "secret", 1, [], Now);
        SeedUser("erika");
        SeedUser("max");
        await Policy().SavePolicyAsync(new SecondFactorPolicy { Mode = SecondFactorMode.Required, AppliesToAll = true });

        var vm = Vm(admin);
        await vm.LoadAsync();

        Assert.Equal(3, vm.RequiredCount);
        Assert.Equal(2, vm.RequiredNotEnrolledCount);
        Assert.Equal(1, vm.EnrolledCount);
    }
}
