using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Security.Authentication;
using Kaimo_File_Server.Core.Security.Management;
using Kaimo_File_Server.Core.Services.Management;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Kaimo_File_Server.Infrastructure.Services.Identity;

namespace Kaimo_File_Server.Web.Tests.Pages;

/// <summary>
/// Settings → Access restrictions → SMB card (AP8c): the client networks Samba accepts. Loads
/// and saves <see cref="AccessRestrictionSettings.SmbAllowedNetworks"/>, validates every line,
/// re-checks ManageSystemSettings on save, and saving a rule never drops the SMB list.
/// </summary>
public sealed class AccessSettingsSmbNetworksTests
{
    private readonly UserContext _actor = new(new User(Guid.NewGuid(), "Admin", "admin", "pw", "nt"), [], [], []);
    private readonly Mock<IConfigRepository> _config = new();
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private AccessRestrictionSettings _stored = new();
    private List<PrincipalSummary> _users = [];
    private int _saves;

    public AccessSettingsSmbNetworksTests()
    {
        _config.Setup(c => c.GetAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .ReturnsAsync(() => _stored);
        _config.Setup(c => c.SetAsync(AccessRestrictionSettings.ConfigKey, It.IsAny<AccessRestrictionSettings>()))
            .Callback((string _, AccessRestrictionSettings value) => { _stored = value; _saves++; })
            .Returns(Task.CompletedTask);
        _config.Setup(c => c.GetAsync(ServerDomainSettings.ConfigKey, It.IsAny<ServerDomainSettings>()))
            .ReturnsAsync(new ServerDomainSettings());
        SetPermission(true);
    }

    private void SetPermission(bool canManage)
        => _mgmtAuth.Setup(m => m.GetEffectivePermissionsAtAsync(It.IsAny<UserContext>(), ScopeType.Global, Guid.Empty))
            .ReturnsAsync(canManage ? ManagementPermission.ManageSystemSettings : ManagementPermission.None);

    private AccessSettingsViewModel Create()
    {
        var authState = new Mock<AuthenticationStateProvider>();
        authState.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
        var contexts = new Mock<IUserContextFactory>();
        contexts.Setup(c => c.CreateByUsernameAsync("admin")).ReturnsAsync(_actor);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetSummariesAsync()).ReturnsAsync(() => _users.ToList());
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetAllAsync()).ReturnsAsync([]);
        var roles = new Mock<IRoleRepository>();
        roles.Setup(r => r.GetAllAsync()).ReturnsAsync([]);
        var principals = new PrincipalDirectoryCache(
            () => PrincipalDirectoryCache.LoadAsync(users.Object, groups.Object, roles.Object), TimeSpan.Zero);
        var departments = new Mock<IDepartmentRepository>();
        departments.Setup(d => d.GetAllAsync()).ReturnsAsync([]);
        var access = new AccessRestrictionService(_config.Object, contexts.Object, departments.Object,
            _cache);

        return new AccessSettingsViewModel(authState.Object, contexts.Object, _mgmtAuth.Object, _config.Object,
            access, principals, departments.Object,
            new ClientConnectionInfo { RemoteAddress = "10.0.0.5", Host = "files.example.com" },
            NullLogger<AccessSettingsViewModel>.Instance);
    }

    [Fact]
    public async Task Load_ShowsTheStoredNetworksOnePerLine()
    {
        _stored = new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8", "192.168.1.20/32"] };
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("10.0.0.0/8" + Environment.NewLine + "192.168.1.20/32", vm.SmbNetworks);
    }

    [Fact]
    public async Task Save_StoresNormalizedNetworksAndKeepsTheRules()
    {
        var rule = new AccessRule { Name = "Office", UserIds = [Guid.NewGuid()], Networks = ["10.0.0.0/8"] };
        _stored = new AccessRestrictionSettings { Rules = [rule] };
        var vm = Create();
        await vm.LoadAsync();

        vm.SmbNetworks = " 192.168.1.20 \n\nfd00::/8\r\n192.168.1.20/32";
        await vm.SaveSmbNetworksAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(["192.168.1.20/32", "fd00::/8"], _stored.SmbAllowedNetworks);
        Assert.Equal(rule.Id, Assert.Single(_stored.Rules).Id);
    }

    [Fact]
    public async Task Save_InvalidLine_IsRejectedWithoutSaving()
    {
        var vm = Create();
        await vm.LoadAsync();

        vm.SmbNetworks = "10.0.0.0/8\nnot-a-network";
        await vm.SaveSmbNetworksAsync();

        Assert.Contains("not-a-network", vm.ErrorMessage);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Save_MoreNetworksThanTheLimit_IsRejectedWithoutSaving()
    {
        var vm = Create();
        await vm.LoadAsync();

        vm.SmbNetworks = string.Join("\n", Enumerable.Range(0, AccessRestrictionSettings.MaxSmbAllowedNetworks + 1)
            .Select(i => $"10.{i / 256}.{i % 256}.0/24"));
        await vm.SaveSmbNetworksAsync();

        Assert.Contains(AccessRestrictionSettings.MaxSmbAllowedNetworks.ToString(), vm.ErrorMessage);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Save_EmptyText_AllowsEveryNetwork()
    {
        _stored = new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8"] };
        var vm = Create();
        await vm.LoadAsync();

        vm.SmbNetworks = "  ";
        await vm.SaveSmbNetworksAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Empty(_stored.SmbAllowedNetworks);
    }

    [Fact]
    public async Task Save_WithoutPermission_WritesNothing()
    {
        var vm = Create();
        await vm.LoadAsync();
        SetPermission(false);

        vm.SmbNetworks = "10.0.0.0/8";
        await vm.SaveSmbNetworksAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task SavingARule_KeepsTheSmbNetworks()
    {
        _stored = new AccessRestrictionSettings { SmbAllowedNetworks = ["10.0.0.0/8"] };
        var vm = Create();
        await vm.LoadAsync();

        vm.NewRule();
        vm.Editing!.Name = "Office";
        vm.Editing.UserIds = [Guid.NewGuid()];
        vm.EditNetworks = "10.0.0.0/8";
        await vm.SaveRuleAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Single(_stored.Rules);
        Assert.Equal(["10.0.0.0/8"], _stored.SmbAllowedNetworks);
    }

    [Fact]
    public async Task SavingTheSmbNetworks_AfterARuleWasSaved_KeepsThatRule()
    {
        var vm = Create();
        await vm.LoadAsync();
        vm.NewRule();
        vm.Editing!.Name = "Office";
        vm.Editing.UserIds = [Guid.NewGuid()];
        vm.EditNetworks = "10.0.0.0/8";
        await vm.SaveRuleAsync();

        vm.SmbNetworks = "192.168.0.0/16";
        await vm.SaveSmbNetworksAsync();

        Assert.Single(_stored.Rules);
        Assert.Equal(["192.168.0.0/16"], _stored.SmbAllowedNetworks);
    }

    // Two admins (or tabs) with the tab open: saving one list must not revert the other's change.
    [Fact]
    public async Task SavingARule_KeepsSmbNetworksSavedMeanwhileElsewhere()
    {
        var first = Create();
        await first.LoadAsync();
        var second = Create();
        await second.LoadAsync();
        second.SmbNetworks = "10.8.0.0/16";
        await second.SaveSmbNetworksAsync();

        first.NewRule();
        first.Editing!.Name = "Office";
        first.Editing.UserIds = [Guid.NewGuid()];
        first.EditNetworks = "10.0.0.0/8";
        await first.SaveRuleAsync();

        Assert.Null(first.ErrorMessage);
        Assert.Single(_stored.Rules);
        Assert.Equal(["10.8.0.0/16"], _stored.SmbAllowedNetworks);
    }

    [Fact]
    public async Task SavingTheSmbNetworks_KeepsRulesSavedMeanwhileElsewhere()
    {
        var first = Create();
        await first.LoadAsync();
        var second = Create();
        await second.LoadAsync();
        second.NewRule();
        second.Editing!.Name = "Office";
        second.Editing.UserIds = [Guid.NewGuid()];
        second.EditNetworks = "10.0.0.0/8";
        await second.SaveRuleAsync();

        first.SmbNetworks = "192.168.0.0/16";
        await first.SaveSmbNetworksAsync();

        Assert.Null(first.ErrorMessage);
        Assert.Equal("Office", Assert.Single(_stored.Rules).Name);
        Assert.Equal(["192.168.0.0/16"], _stored.SmbAllowedNetworks);
        // The tab now shows the rule saved elsewhere, so its next rule save keeps it.
        Assert.Equal("Office", Assert.Single(first.Rules).Name);
    }

    [Fact]
    public async Task Load_OffersUsersWithEmailHint_AndRevalidationPicksUpNewUsers()
    {
        _users = [new PrincipalSummary(Guid.NewGuid(), "Alice", "alice", "alice@example.com")];
        var vm = Create();
        await vm.LoadAsync();
        Assert.Equal("alice@example.com", Assert.Single(vm.UserItems).Hint);

        _users = [.. _users, new PrincipalSummary(Guid.NewGuid(), "Bob", "bob")];

        Assert.True(await vm.RevalidatePrincipalsAsync());
        Assert.Equal(2, vm.UserItems.Count);
        Assert.Equal("bob", vm.UserItems[1].Hint);
    }

    [Fact]
    public async Task RevalidatePrincipals_WithoutPermission_DoesNothing()
    {
        SetPermission(false);
        _users = [new PrincipalSummary(Guid.NewGuid(), "Alice", "alice")];
        var vm = Create();
        await vm.LoadAsync();

        Assert.False(await vm.RevalidatePrincipalsAsync());
        Assert.Empty(vm.UserItems);
    }
}
