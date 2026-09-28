using Bunit;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Notifications;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Kaimo_File_Server.Web.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Mail notifications end to end against the real schema: outbox → rules → recipients →
/// rendered deliveries → SMTP (faked), plus the renderer's sandboxing and the permission gates.
/// </summary>
public sealed class MailNotificationTests : DatabaseTestBase
{
    private readonly NotificationRepository _repo;
    private readonly ServiceProvider _services;
    private readonly SmtpConfigStore _smtp;
    private readonly FakeSender _sender = new();
    private readonly NotificationMailComposer _composer;
    private readonly NotificationDispatcherService _dispatcher;

    public MailNotificationTests()
    {
        _repo = new NotificationRepository(DbFactory);
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfigRepository>(new ConfigRepository(DbFactory, new MemoryCache(new MemoryCacheOptions())));
        collection.AddSingleton<IUserRepository>(UserRepo());
        collection.AddSingleton<IGroupRepository>(GroupRepo());
        collection.AddSingleton<IRoleRepository>(RoleRepo());
        collection.AddSingleton<IScopedRoleAssignmentRepository>(ScopedRoleRepo());
        collection.AddScoped<NotificationRecipientResolver>();
        _services = collection.BuildServiceProvider();

        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        _smtp = new SmtpConfigStore(scopes);
        _composer = new NotificationMailComposer(scopes, _repo, _smtp, new MailTemplateRenderer());
        _dispatcher = new NotificationDispatcherService(scopes, _repo, new NotificationDispatchSignal(), _smtp,
            _sender, _composer, TimeProvider.System, NullLogger<NotificationDispatcherService>.Instance);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private sealed class FakeSender : ISmtpMailSender
    {
        public List<MailMessageModel> Sent { get; } = [];
        public Exception? Fail { get; set; }
        public Action? OnSend { get; set; }

        public Task SendAsync(MailMessageModel message, CancellationToken ct = default)
        {
            if (Fail is not null) throw Fail;
            OnSend?.Invoke();
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<SmtpTestResult> TestAsync(SmtpSettings draft, string? plainPassword, MailMessageModel message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(new SmtpTestResult(true, null));
        }
    }

    private User SeedMailUser(string username, string? email, bool enabled = true)
    {
        var user = new User(Guid.NewGuid(), username.ToUpperInvariant(), username, "pw-hash", "nt-hash",
            email: email, isEnabled: enabled);
        using var db = NewContext();
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private async Task<MailRule> AddRuleAsync(string eventType, int throttle = 0, params RecipientSpec[] recipients)
    {
        var rule = new MailRule { EventType = eventType, Name = "r", Enabled = true, ThrottleMinutes = throttle };
        rule.SetRecipients(recipients);
        await _repo.SaveRuleAsync(rule);
        return rule;
    }

    private Task ConfigureSmtpAsync()
        => _smtp.SetAsync(new SmtpSettings { Enabled = true, Host = "smtp.test", FromAddress = "files@example.com" });

    private async Task<List<MailDelivery>> DeliveriesAsync()
    {
        await using var db = NewContext();
        return await db.MailDeliveries.AsNoTracking().ToListAsync();
    }

    // ───────────────────────── Dispatcher ─────────────────────────

    [Fact]
    public async Task Event_WithEnabledRule_IsRenderedForContextRecipient_AndSentOnceSmtpIsConfigured()
    {
        var creator = SeedMailUser("erika", "erika@example.com");
        await AddRuleAsync(NotificationEventCatalog.UploadReceived, 0,
            new RecipientSpec(RecipientKind.ContextRole, NotificationContextRoles.LinkCreator));
        await _repo.AddEventAsync(NotificationEvents.UploadReceived(Guid.NewGuid(), "Bewerbungen", creator.Id, "cv.pdf", 2048));

        await _dispatcher.ProcessEventsAsync(default);

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal("erika@example.com", delivery.ToAddress);
        Assert.Equal(MailDeliveryStatus.Pending, delivery.Status);
        Assert.Contains("Bewerbungen", delivery.Subject);
        Assert.Contains("cv.pdf", delivery.HtmlBody);
        await using (var db = NewContext())
            Assert.Equal(NotificationEventStatus.Done, (await db.NotificationEvents.SingleAsync()).Status);

        // SMTP not configured yet: nothing is sent, the delivery keeps waiting.
        await _dispatcher.SendDueAsync(default);
        Assert.Empty(_sender.Sent);
        Assert.Equal(MailDeliveryStatus.Pending, (await DeliveriesAsync()).Single().Status);

        await ConfigureSmtpAsync();
        await _dispatcher.SendDueAsync(default);
        Assert.Single(_sender.Sent);
        Assert.Equal(MailDeliveryStatus.Sent, (await DeliveriesAsync()).Single().Status);
    }

    [Fact]
    public async Task Event_WithoutRule_IsSkipped()
    {
        await _repo.AddEventAsync(NotificationEvents.BackupFailed("Scheduled", "boom"));

        await _dispatcher.ProcessEventsAsync(default);

        Assert.Empty(await DeliveriesAsync());
        await using var db = NewContext();
        Assert.Equal(NotificationEventStatus.Skipped, (await db.NotificationEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Throttle_SendsOnlyOneMailPerDedupKeyAndRecipient()
    {
        var user = SeedMailUser("max", "max@example.com");
        await AddRuleAsync(NotificationEventCatalog.AccountLocked, 60,
            new RecipientSpec(RecipientKind.ContextRole, NotificationContextRoles.Affected));

        await _repo.AddEventAsync(NotificationEvents.AccountLocked(user.Id, "max", "203.0.113.7", TimeSpan.FromMinutes(15)));
        await _dispatcher.ProcessEventsAsync(default);
        await _repo.AddEventAsync(NotificationEvents.AccountLocked(user.Id, "max", "203.0.113.7", TimeSpan.FromMinutes(15)));
        await _dispatcher.ProcessEventsAsync(default);

        Assert.Single(await DeliveriesAsync());
    }

    [Fact]
    public async Task TwoRulesForTheSameAddress_ProduceOneMail()
    {
        var user = SeedMailUser("anna", "anna@example.com");
        var spec = new RecipientSpec(RecipientKind.User, user.Id.ToString());
        await AddRuleAsync(NotificationEventCatalog.CertificateRenewed, 0, spec);
        await AddRuleAsync(NotificationEventCatalog.CertificateRenewed, 0, spec,
            new RecipientSpec(RecipientKind.ExternalAddress, "ops@example.com"));

        await _repo.AddEventAsync(NotificationEvents.CertificateRenewed(DateTime.UtcNow.AddYears(1)));
        await _dispatcher.ProcessEventsAsync(default);

        Assert.Equal(["anna@example.com", "ops@example.com"],
            (await DeliveriesAsync()).Select(d => d.ToAddress).Order());
    }

    [Fact]
    public async Task FailedSend_IsRetriedWithBackoff_AndGivenUpAfterMaxAttempts()
    {
        await ConfigureSmtpAsync();
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0,
            new RecipientSpec(RecipientKind.ExternalAddress, "ops@example.com"));
        await _repo.AddEventAsync(NotificationEvents.BackupFailed("Scheduled", "boom"));
        await _dispatcher.ProcessEventsAsync(default);

        _sender.Fail = new MailSendException("server down", permanent: false);
        await _dispatcher.SendDueAsync(default);

        var failed = (await DeliveriesAsync()).Single();
        Assert.Equal(MailDeliveryStatus.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.True(failed.NextAttemptUtc > DateTime.UtcNow);
        Assert.Equal("server down", (await _smtp.GetStatusAsync()).LastError);

        // Fast-forward to the last allowed attempt.
        failed.AttemptCount = NotificationDispatcherService.MaxDeliveryAttempts - 1;
        failed.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        await _repo.UpdateDeliveryAsync(failed);
        await _dispatcher.SendDueAsync(default);

        Assert.Equal(MailDeliveryStatus.Dead, (await DeliveriesAsync()).Single().Status);
    }

    [Fact]
    public async Task PermanentFailure_IsDeadImmediately()
    {
        await ConfigureSmtpAsync();
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0,
            new RecipientSpec(RecipientKind.ExternalAddress, "nobody@example.com"));
        await _repo.AddEventAsync(NotificationEvents.BackupFailed("Scheduled", "boom"));
        await _dispatcher.ProcessEventsAsync(default);

        _sender.Fail = new MailSendException("recipient rejected", permanent: true);
        await _dispatcher.SendDueAsync(default);

        Assert.Equal(MailDeliveryStatus.Dead, (await DeliveriesAsync()).Single().Status);
    }

    [Fact]
    public async Task TransportFailure_PausesSending_KeepsRetryBudget_AndResumesWhenSettingsChange()
    {
        await ConfigureSmtpAsync();
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0,
            new RecipientSpec(RecipientKind.ExternalAddress, "a@example.com"),
            new RecipientSpec(RecipientKind.ExternalAddress, "b@example.com"));
        await _repo.AddEventAsync(NotificationEvents.BackupFailed("Scheduled", "boom"));
        await _dispatcher.ProcessEventsAsync(default);

        var attempts = 0;
        _sender.OnSend = () => { attempts++; throw new MailSendException("unreachable", false, transport: true); };
        await _dispatcher.SendDueAsync(default);

        // One connection attempt for the whole batch; no mail spent a retry.
        Assert.Equal(1, attempts);
        Assert.All(await DeliveriesAsync(), d =>
        {
            Assert.Equal(MailDeliveryStatus.Failed, d.Status);
            Assert.Equal(0, d.AttemptCount);
        });

        // Paused: the next cycle does not even try.
        await _dispatcher.SendDueAsync(default);
        Assert.Equal(1, attempts);

        // A settings change lifts the pause.
        _sender.OnSend = null;
        await _smtp.SetAsync(new SmtpSettings { Enabled = true, Host = "smtp2.test", FromAddress = "files@example.com" });
        await _dispatcher.SendDueAsync(default);
        Assert.All(await DeliveriesAsync(), d => Assert.Equal(MailDeliveryStatus.Sent, d.Status));
    }

    [Fact]
    public void AccountLocked_OfUnknownUsers_ShareOneDedupKey()
    {
        var a = NotificationEvents.AccountLocked(null, "ghost1", null, TimeSpan.FromMinutes(15));
        var b = NotificationEvents.AccountLocked(null, "ghost2", null, TimeSpan.FromMinutes(15));
        var known = NotificationEvents.AccountLocked(Guid.NewGuid(), "Erika", null, TimeSpan.FromMinutes(15));

        Assert.Equal(a.DedupKey, b.DedupKey);
        Assert.Equal("lock:erika", known.DedupKey);
    }

    [Fact]
    public void Backoff_GrowsExponentially_AndIsCapped()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationDispatcherService.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(4), NotificationDispatcherService.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(6), NotificationDispatcherService.Backoff(20));
    }

    [Fact]
    public async Task DefaultRules_AreSeededDisabledOnce_AndDeletedOnesStayDeleted()
    {
        await _dispatcher.SeedDefaultRulesAsync(default);
        var rules = await _repo.GetRulesAsync();
        Assert.Equal(NotificationEventCatalog.All.Count, rules.Count);
        Assert.All(rules, r => Assert.False(r.Enabled));

        await _repo.DeleteRuleAsync(rules[0].Id);
        await _dispatcher.SeedDefaultRulesAsync(default);

        Assert.Equal(NotificationEventCatalog.All.Count - 1, (await _repo.GetRulesAsync()).Count);
    }

    // ───────────────────────── Recipients ─────────────────────────

    [Fact]
    public async Task Resolver_ExpandsGroupsAndPermissionHolders_SkipsDisabledAndAddresslessUsers_AndDedupes()
    {
        var dept = SeedDepartment("Global");
        var alice = SeedMailUser("alice", "alice@example.com");
        var bob = SeedMailUser("bob", "bob@example.com", enabled: false);
        var carl = SeedMailUser("carl", null);
        var dora = SeedMailUser("dora", "dora@example.com");

        var group = await GroupRepo().CreateAsync(new Group(Guid.NewGuid(), "team", dept.Id));
        foreach (var member in new[] { alice, bob, carl })
            await GroupRepo().AddMemberAsync(group.Id, member.Id);

        var role = await RoleRepo().CreateAsync(new Role(Guid.NewGuid(), "backups", ManagementPermission.ManageBackups));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(dora.Id, role.Id));

        using var scope = _services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<NotificationRecipientResolver>();
        var result = await resolver.ResolveAsync(
        [
            new(RecipientKind.Group, group.Id.ToString()),
            new(RecipientKind.PermissionHolder, nameof(ManagementPermission.ManageBackups)),
            new(RecipientKind.User, alice.Id.ToString()),
            new(RecipientKind.ExternalAddress, "ALICE@example.com"),
            new(RecipientKind.ExternalAddress, "not an address"),
            new(RecipientKind.ContextRole, NotificationContextRoles.Affected),
        ], new Dictionary<string, Guid> { [NotificationContextRoles.Affected] = bob.Id });

        Assert.Equal(["alice@example.com", "dora@example.com"], result.Select(r => r.Address).Order());
    }

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("Name <a@example.com>", false)]
    [InlineData("a@example.com, b@example.com", false)]
    [InlineData("no-at-sign", false)]
    public void AddressValidation_AcceptsOnlySinglePlainAddresses(string value, bool valid)
        => Assert.Equal(valid, NotificationRecipientResolver.TryNormalizeAddress(value, out _));

    // ───────────────────────── Renderer ─────────────────────────

    private static readonly MailTemplateRenderer Renderer = new();

    [Fact]
    public async Task Renderer_ResolvesDottedPlaceholders_AndEncodesValues()
    {
        var mail = await Renderer.RenderAsync(
            new MailTemplateSource("Hi {{ user.name }}", "<p>{{ user.name }}</p>"),
            MailLayoutSettings.Default(),
            new Dictionary<string, string> { ["user.name"] = "<script>x</script>" }, "en", null);

        Assert.Contains("&lt;script&gt;", mail.Html);
        Assert.DoesNotContain("<script>", mail.Html);
        Assert.Equal("Hi <script>x</script>", mail.Subject); // plain text, never HTML
        Assert.Contains(MailLayoutSettings.DefaultAccentColor, mail.Html); // wrapped into the layout
    }

    [Fact]
    public async Task Renderer_StripsLineBreaksFromTheSubject()
    {
        var mail = await Renderer.RenderAsync(
            new MailTemplateSource("{{ x }}", "<p></p>"), MailLayoutSettings.Default(),
            new Dictionary<string, string> { ["x"] = "a\r\nBcc: evil@example.com" }, "en", null);

        Assert.DoesNotContain('\n', mail.Subject);
        Assert.DoesNotContain('\r', mail.Subject);
    }

    [Fact]
    public async Task Renderer_BoundsRunawayLoops()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Renderer.RenderAsync(
            new MailTemplateSource("s", "{% for i in (1..10000000) %}x{% endfor %}"),
            MailLayoutSettings.Default(), new Dictionary<string, string>(), "en", null));
    }

    [Fact]
    public void Validate_ReportsParseErrors()
    {
        Assert.Null(MailTemplateRenderer.Validate("{{ ok }}"));
        Assert.NotNull(MailTemplateRenderer.Validate("{% if %}"));
    }

    [Fact]
    public void HtmlToText_KeepsLinksAndLineBreaks()
    {
        var text = MailTemplateRenderer.HtmlToText("<p>Hello &amp; welcome</p><p><a href=\"https://x.test\">here</a></p>");
        Assert.Equal("Hello & welcome\n\nhere (https://x.test)", text);
    }

    [Fact]
    public void BuiltInTemplates_CoverEveryCatalogEventInEveryLanguage_AndParse()
    {
        Assert.True(DefaultMailTemplates.CoversCatalog());
        foreach (var definition in NotificationEventCatalog.All)
            foreach (var language in DefaultMailTemplates.Languages)
            {
                var template = DefaultMailTemplates.Get(definition.Type, language);
                Assert.Null(MailTemplateRenderer.Validate(template.Subject));
                Assert.Null(MailTemplateRenderer.Validate(template.Html));
            }
        Assert.Null(MailTemplateRenderer.Validate(DefaultMailTemplates.Layout));
    }

    [Fact]
    public void Layout_RejectsUnsafeAccentAndOversizedLogo()
    {
        var layout = new MailLayoutSettings
        {
            AccentColor = "red;background:url(x)",
            LogoDataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[MailLayoutSettings.MaxLogoBytes + 1]),
            PublicBaseUrl = "javascript:alert(1)",
        };
        layout.Normalize();

        Assert.Equal(MailLayoutSettings.DefaultAccentColor, layout.AccentColor);
        Assert.Null(layout.LogoDataUri);
        Assert.Equal(string.Empty, layout.PublicBaseUrl);
    }

    // ───────────────────────── Publisher ─────────────────────────

    [Fact]
    public async Task Publisher_NeverThrows_EvenWhenTheStoreFails()
    {
        var repo = new Mock<INotificationRepository>();
        repo.Setup(r => r.GetEnabledRuleTypesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var publisher = new DbNotificationPublisher(repo.Object, new NotificationDispatchSignal(),
            NullLogger<DbNotificationPublisher>.Instance);

        await publisher.PublishAsync(NotificationEvents.BackupFailed("Manual", "x"));
    }

    [Fact]
    public async Task Publisher_SkipsEventsWithoutEnabledRule_AndStoresOthers()
    {
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0, new RecipientSpec(RecipientKind.ExternalAddress, "a@example.com"));
        var publisher = new DbNotificationPublisher(_repo, new NotificationDispatchSignal(),
            NullLogger<DbNotificationPublisher>.Instance);

        await publisher.PublishAsync(NotificationEvents.BackupSucceeded("Manual", "f.dump", 1));
        await publisher.PublishAsync(NotificationEvents.BackupFailed("Manual", "x"));

        await using var db = NewContext();
        Assert.Equal(NotificationEventCatalog.BackupFailed, (await db.NotificationEvents.SingleAsync()).Type);
    }

    [Fact]
    public async Task Publisher_InDemoMode_StoresNothing()
    {
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0, new RecipientSpec(RecipientKind.ExternalAddress, "a@example.com"));
        var publisher = new DbNotificationPublisher(_repo, new NotificationDispatchSignal(),
            NullLogger<DbNotificationPublisher>.Instance, demo: new DemoModeOptions { ReadOnly = true });

        await publisher.PublishAsync(NotificationEvents.BackupFailed("Manual", "x"));

        await using var db = NewContext();
        Assert.Empty(db.NotificationEvents);
    }

    // ───────────────────────── Mail-server settings page ─────────────────────────

    private MailServerSettingsViewModel MailServerVm(ManagementPermission permissions, out ICredentialVault vault)
    {
        var actor = SeedMailUser("admin" + Guid.NewGuid().ToString("N")[..6], "admin@example.com");
        var mgmt = new Mock<IManagementAuthService>();
        mgmt.Setup(m => m.GetEffectivePermissionsAtAsync(It.IsAny<UserContext>(), ScopeType.Global, Guid.Empty))
            .ReturnsAsync(permissions);
        vault = new DataProtectionCredentialVault(new EphemeralDataProtectionProvider());
        return new MailServerSettingsViewModel(
            AuthStateFor(actor.Username), UserContextFactoryFor((actor.Username, ContextFor(actor))).Object,
            mgmt.Object, _smtp, _sender, vault, _composer, NullLogger<MailServerSettingsViewModel>.Instance);
    }

    [Fact]
    public async Task MailServerSettings_WithoutPermission_IsDenied_AndCannotSave()
    {
        var vm = MailServerVm(ManagementPermission.ManageNotifications, out _);
        await vm.LoadAsync();
        Assert.False(vm.CanManage);

        vm.Settings.Host = "smtp.evil.test";
        await vm.SaveAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(string.Empty, (await _smtp.GetAsync()).Host);
    }

    [Fact]
    public async Task MailServerSettings_StoresThePasswordEncrypted_AndKeepsItWhenLeftEmpty()
    {
        var vm = MailServerVm(ManagementPermission.ManageMailServer, out var vault);
        await vm.LoadAsync();
        Assert.True(vm.CanManage);

        vm.Settings.Enabled = true;
        vm.Settings.Host = "smtp.example.com";
        vm.Settings.FromAddress = "files@example.com";
        vm.Settings.Username = "mailer";
        vm.NewPassword = "S3cret!";
        await vm.SaveAsync();
        Assert.Null(vm.ErrorMessage);

        var stored = await _smtp.GetAsync();
        Assert.NotNull(stored.EncryptedPassword);
        Assert.DoesNotContain("S3cret!", stored.EncryptedPassword);
        Assert.Equal("S3cret!", vault.Unprotect<string>(stored.EncryptedPassword!, SmtpSettings.PasswordContext));
        await using (var db = NewContext())
            Assert.DoesNotContain(db.ConfigSettings.AsEnumerable(), s => s.Value.Contains("S3cret!"));

        // Saving again without a new password keeps the stored one.
        await vm.SaveAsync();
        Assert.Equal(stored.EncryptedPassword, (await _smtp.GetAsync()).EncryptedPassword);
    }

    [Fact]
    public async Task MailServerSettings_StoredPassword_NeverFollowsAChangedServer()
    {
        var vm = MailServerVm(ManagementPermission.ManageMailServer, out var vault);
        await vm.LoadAsync();
        vm.Settings.Enabled = true;
        vm.Settings.Host = "smtp.example.com";
        vm.Settings.FromAddress = "files@example.com";
        vm.Settings.Username = "mailer";
        vm.NewPassword = "S3cret!";
        await vm.SaveAsync();
        Assert.Null(vm.ErrorMessage);

        // Save: a new host without re-entering the password is refused.
        vm.Settings.Host = "smtp.evil.test";
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal("smtp.example.com", (await _smtp.GetAsync()).Host);

        // Test mail: the real sender refuses before connecting anywhere.
        var sender = new MailKitSmtpMailSender(_smtp, vault, NullLogger<MailKitSmtpMailSender>.Instance);
        var draft = (await _smtp.GetAsync()) with { Host = "smtp.evil.test" };
        var result = await sender.TestAsync(draft, null, new MailMessageModel("x@example.com", "s", "<p>h</p>", "t"));
        Assert.False(result.Success);
        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.ResourceManager.GetString("Web_MailServer_Error_PasswordRequired"),
            result.Error);
    }

    // ───────────────────────── Page rendering (bUnit smoke) ─────────────────────────

    private Bunit.TestContext PageContext(ManagementPermission permissions)
    {
        var actor = SeedMailUser("page" + Guid.NewGuid().ToString("N")[..6], "page@example.com");
        var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        Bunit.TestDoubles.FakeAuthorizationExtensions.AddTestAuthorization(ctx).SetAuthorized(actor.Username);

        var mgmt = new Mock<IManagementAuthService>();
        mgmt.Setup(m => m.GetEffectivePermissionsAtAsync(It.IsAny<UserContext>(), ScopeType.Global, Guid.Empty))
            .ReturnsAsync(permissions);
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(FindWebRoot());

        var services = ctx.Services;
        services.AddSingleton(new AssetProvider(env.Object));
        services.AddSingleton(_services.GetRequiredService<IConfigRepository>());
        services.AddSingleton<DateFormatService>();
        services.AddSingleton(UserContextFactoryFor((actor.Username, ContextFor(actor))).Object);
        services.AddSingleton(mgmt.Object);
        services.AddScoped(sp => new NotificationsViewModel(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(),
            sp.GetRequiredService<IUserContextFactory>(), mgmt.Object, _repo, UserRepo(), GroupRepo(),
            sp.GetRequiredService<IConfigRepository>(), _smtp, _sender, _composer, new NotificationDispatchSignal(),
            NullLogger<NotificationsViewModel>.Instance));
        services.AddScoped(sp => new MailServerSettingsViewModel(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(),
            sp.GetRequiredService<IUserContextFactory>(), mgmt.Object, _smtp, _sender,
            new DataProtectionCredentialVault(new EphemeralDataProtectionProvider()), _composer,
            NullLogger<MailServerSettingsViewModel>.Instance));
        return ctx;
    }

    private static string FindWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Kaimo_File_Server.Web", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("wwwroot of the web project not found");
    }

    [Fact]
    public async Task NotificationsPage_RendersAllTabs()
    {
        await _dispatcher.SeedDefaultRulesAsync(default);
        await AddRuleAsync(NotificationEventCatalog.BackupFailed, 0, new RecipientSpec(RecipientKind.ExternalAddress, "ops@example.com"));
        await _repo.AddEventAsync(NotificationEvents.BackupFailed("Scheduled", "boom"));
        await _dispatcher.ProcessEventsAsync(default);

        using var ctx = PageContext(ManagementPermission.ManageNotifications);
        var cut = ctx.RenderComponent<Kaimo_File_Server.Web.Components.Pages.Notifications.Notifications>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".notif-cat__head")), TimeSpan.FromSeconds(5));

        // Rules: categories start collapsed; expanding one shows its events and rules.
        Assert.Empty(cut.FindAll(".notif-event"));
        Assert.Equal("false", cut.Find(".notif-cat__head").GetAttribute("aria-expanded"));
        cut.Find(".notif-cat__head").Click();
        Assert.Equal("true", cut.Find(".notif-cat__head").GetAttribute("aria-expanded"));
        Assert.NotEmpty(cut.FindAll(".notif-event"));

        // "Expand all" opens every category, including the one with the backup rule.
        cut.Find(".notif-tree-toolbar > button").Click();
        Assert.All(cut.FindAll(".notif-cat__head"), h => Assert.Equal("true", h.GetAttribute("aria-expanded")));
        Assert.Contains("ops@example.com", cut.Markup);

        // Open the rule editor of the first rule (user.created): the involved person is named
        // for this event, and the plain-language summary lists who gets the mail.
        cut.Find(".notif-rule[role=button]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".notif-rule-editor .workspace-heading")));
        var involved = NotificationsViewModel.RoleLabel(NotificationEventCatalog.UserCreated, NotificationContextRoles.Affected);
        Assert.NotEqual(NotificationsViewModel.ContextRoleLabel(NotificationContextRoles.Affected), involved);
        Assert.Contains(involved, cut.Find(".notif-roles").TextContent);
        Assert.Contains(involved, cut.Find(".notif-rule-summary").TextContent);

        // Templates, layout and log each render their workspace.
        cut.Find(".notif-tabs .detail-tabs button:nth-of-type(2)").Click();
        cut.WaitForAssertion(() => Assert.Contains("srcdoc", cut.Find(".notif-preview__frame").OuterHtml));
        cut.Find(".notif-tabs .detail-tabs button:nth-of-type(3)").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".notif-layout")));
        cut.Find(".notif-tabs .detail-tabs button:nth-of-type(4)").Click();
        cut.WaitForAssertion(() => Assert.Contains("ops@example.com", cut.Markup));
    }

    [Fact]
    public void NotificationsPage_WithoutPermission_ShowsTheBanner()
    {
        using var ctx = PageContext(ManagementPermission.ManageMailServer);
        var cut = ctx.RenderComponent<Kaimo_File_Server.Web.Components.Pages.Notifications.Notifications>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".error-banner")), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll(".notif-tabs"));
    }

    [Fact]
    public void MailServerSettingsTab_RendersTheForm()
    {
        using var ctx = PageContext(ManagementPermission.ManageMailServer);
        var cut = ctx.RenderComponent<Kaimo_File_Server.Web.Components.Pages.Settings.Components.MailServerSettings>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#smtp-host")), TimeSpan.FromSeconds(5));
        Assert.Equal("page@example.com", cut.Find("#smtp-test-to").GetAttribute("value"));
    }

    [Fact]
    public async Task MailServerSettings_TestMail_GoesToTheEnteredAddress()
    {
        var vm = MailServerVm(ManagementPermission.ManageMailServer, out _);
        await vm.LoadAsync();
        vm.TestAddress = "check@example.com";

        await vm.SendTestAsync();

        Assert.True(vm.TestResult?.Success);
        Assert.Equal("check@example.com", Assert.Single(_sender.Sent).ToAddress);
    }
}
