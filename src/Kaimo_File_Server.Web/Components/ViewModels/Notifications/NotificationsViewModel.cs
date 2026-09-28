using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Notifications;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Services.Notifications;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the <c>/notifications</c> page: notification rules, mail templates per event and
/// language (with live preview), the shared mail layout and the delivery log. Everything is
/// gated by <see cref="ManagementPermission.ManageNotifications"/> at global scope, re-checked
/// on every mutation.
/// </summary>
public sealed class NotificationsViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    INotificationRepository repository,
    IUserRepository users,
    IGroupRepository groups,
    IConfigRepository config,
    ISmtpConfigStore smtp,
    ISmtpMailSender sender,
    NotificationMailComposer composer,
    NotificationDispatchSignal signal,
    ILogger<NotificationsViewModel> logger)
{
    public const int LogSize = 200;

    private UserContext? _actor;
    private MailEnvironment? _environment;

    public bool IsLoading { get; private set; } = true;
    public bool CanAccessPage { get; private set; }
    public bool SmtpUsable { get; private set; }
    public int PendingCount { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            CanAccessPage = await HasPermissionAsync();
            if (!CanAccessPage) return;

            var allUsers = (await users.GetAllAsync()).OrderBy(u => u.Name).ToList();
            var allGroups = (await groups.GetAllAsync()).OrderBy(g => g.Name).ToList();
            UserItems = allUsers.Select(u => new PrincipalPickerItem(
                u.Id, u.Name, PrincipalKind.User, string.IsNullOrWhiteSpace(u.Email) ? u.Username : u.Email)).ToList();
            GroupItems = allGroups.Select(g => new PrincipalPickerItem(g.Id, g.Name, PrincipalKind.Group)).ToList();
            _names = allUsers.Select(u => (u.Id, u.Name)).Concat(allGroups.Select(g => (g.Id, g.Name)))
                .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);

            await ReloadRulesAsync();
            await RefreshStatusAsync();
            _environment = await composer.LoadEnvironmentAsync();
            Layout = _environment.Layout with { };
            await LoadTemplatesListAsync();
            await RefreshLogAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the notifications page");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshStatusAsync()
    {
        SmtpUsable = (await smtp.GetAsync()).IsUsable;
        PendingCount = await repository.CountPendingDeliveriesAsync();
    }

    public void ClearMessages()
    {
        SuccessMessage = null;
        ErrorMessage = null;
    }

    // ══════════════════════════════════════════
    //  Rules
    // ══════════════════════════════════════════

    private Dictionary<Guid, string> _names = new();

    public IReadOnlyList<PrincipalPickerItem> UserItems { get; private set; } = [];
    public IReadOnlyList<PrincipalPickerItem> GroupItems { get; private set; } = [];
    public List<MailRule> Rules { get; private set; } = [];
    public Dictionary<Guid, DateTime> LastSentByRule { get; private set; } = new();

    /// <summary>The rule shown in the detail panel (a new, unsaved rule has no row yet).</summary>
    public MailRule? SelectedRule { get; private set; }
    public bool SelectedRuleIsNew { get; private set; }

    public string EditName { get; set; } = string.Empty;
    public bool EditEnabled { get; set; }
    public int EditThrottleMinutes { get; set; }
    public HashSet<string> EditContextRoles { get; private set; } = [];
    public IReadOnlyList<Guid> EditUserIds { get; set; } = [];
    public IReadOnlyList<Guid> EditGroupIds { get; set; } = [];
    public List<CheckboxItem<ManagementPermission>> EditPermissions { get; private set; } = [];
    public string EditExternalAddresses { get; set; } = string.Empty;

    /// <summary>Permission holders a rule can address, with their localized labels.</summary>
    public static IReadOnlyList<(ManagementPermission Permission, string Label)> SelectablePermissions =>
        UserListViewModel.PermissionGroups.SelectMany(g => g.Flags)
            .Select(p => (p.Flag, p.Label))
            .Append((ManagementPermission.SyncAdmin, R("Web_Notifications_Perm_SyncAdmin")))
            .DistinctBy(p => p.Item1)
            .ToList();

    public IEnumerable<IGrouping<string, NotificationEventDefinition>> CatalogByCategory
        => NotificationEventCatalog.All.GroupBy(d => d.Category);

    public IEnumerable<MailRule> RulesFor(string eventType) => Rules.Where(r => r.EventType == eventType);

    public void SelectRule(Guid id)
    {
        var rule = Rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        ClearMessages();
        SelectedRuleIsNew = false;
        StartEdit(rule);
    }

    /// <summary>Opens an unsaved rule for <paramref name="eventType"/> prefilled with the catalog defaults.</summary>
    public void NewRule(string eventType)
    {
        var definition = NotificationEventCatalog.Find(eventType);
        if (definition is null) return;
        ClearMessages();
        var rule = new MailRule
        {
            EventType = eventType,
            Name = R("Web_Notifications_NewRuleName"),
            ThrottleMinutes = definition.DefaultThrottleMinutes,
        };
        rule.SetRecipients(definition.DefaultRecipients);
        SelectedRuleIsNew = true;
        StartEdit(rule);
    }

    public void CloseRule()
    {
        SelectedRule = null;
        SelectedRuleIsNew = false;
    }

    private void StartEdit(MailRule rule)
    {
        SelectedRule = rule;
        EditName = rule.Name;
        EditEnabled = rule.Enabled;
        EditThrottleMinutes = rule.ThrottleMinutes;
        var recipients = rule.GetRecipients();
        EditContextRoles = recipients.Where(r => r.Kind == RecipientKind.ContextRole).Select(r => r.Value).ToHashSet();
        EditUserIds = IdsOf(recipients, RecipientKind.User);
        EditGroupIds = IdsOf(recipients, RecipientKind.Group);
        var permissions = recipients.Where(r => r.Kind == RecipientKind.PermissionHolder).Select(r => r.Value).ToHashSet();
        EditPermissions = SelectablePermissions
            .Select(p => new CheckboxItem<ManagementPermission>(p.Permission, permissions.Contains(p.Permission.ToString())))
            .ToList();
        EditExternalAddresses = string.Join('\n',
            recipients.Where(r => r.Kind == RecipientKind.ExternalAddress).Select(r => r.Value));
    }

    public void ToggleContextRole(string role, bool on)
    {
        if (on) EditContextRoles.Add(role);
        else EditContextRoles.Remove(role);
    }

    public async Task SaveRuleAsync()
    {
        ClearMessages();
        if (SelectedRule is null) return;
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        if (string.IsNullOrWhiteSpace(EditName)) { ErrorMessage = R("Web_Error_NameRequired"); return; }

        var recipients = new List<RecipientSpec>();
        recipients.AddRange(EditContextRoles.Select(r => new RecipientSpec(RecipientKind.ContextRole, r)));
        recipients.AddRange(EditUserIds.Select(id => new RecipientSpec(RecipientKind.User, id.ToString())));
        recipients.AddRange(EditGroupIds.Select(id => new RecipientSpec(RecipientKind.Group, id.ToString())));
        recipients.AddRange(EditPermissions.Where(p => p.IsChecked)
            .Select(p => new RecipientSpec(RecipientKind.PermissionHolder, p.Item.ToString())));

        foreach (var line in EditExternalAddresses.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!NotificationRecipientResolver.TryNormalizeAddress(line, out var address))
            {
                ErrorMessage = string.Format(R("Web_Notifications_InvalidAddress"), line);
                return;
            }
            recipients.Add(new RecipientSpec(RecipientKind.ExternalAddress, address));
        }

        var rule = SelectedRule;
        rule.Name = EditName.Trim();
        rule.Enabled = EditEnabled;
        rule.ThrottleMinutes = Math.Clamp(EditThrottleMinutes, 0, 7 * 24 * 60);
        rule.SetRecipients(recipients);
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedByUserId = _actor?.User.Id;

        await MutateAsync(async () =>
        {
            await repository.SaveRuleAsync(rule);
            await ReloadRulesAsync();
            SelectRule(rule.Id);
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    /// <summary>Quick enable/disable from the rule list.</summary>
    public async Task SetRuleEnabledAsync(Guid id, bool enabled)
    {
        ClearMessages();
        var rule = Rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        rule.Enabled = enabled;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedByUserId = _actor?.User.Id;
        await MutateAsync(async () =>
        {
            await repository.SaveRuleAsync(rule);
            await ReloadRulesAsync();
            if (SelectedRule?.Id == id) EditEnabled = enabled;
        });
    }

    public async Task DeleteRuleAsync()
    {
        ClearMessages();
        if (SelectedRule is null) return;
        if (SelectedRuleIsNew) { CloseRule(); return; }
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        var id = SelectedRule.Id;
        await MutateAsync(async () =>
        {
            await repository.DeleteRuleAsync(id);
            await ReloadRulesAsync();
            CloseRule();
            SuccessMessage = R("Web_Notifications_RuleDeleted");
        });
    }

    /// <summary>A compact, localized description of a rule's recipients for the list.</summary>
    public string RecipientSummary(MailRule rule)
    {
        var parts = RecipientLabels(rule.EventType, rule.GetRecipients());
        return parts.Count == 0 ? R("Web_Notifications_NoRecipients") : string.Join(", ", parts);
    }

    /// <summary>The recipients of the rule being edited, as they stand right now (unsaved).</summary>
    public IReadOnlyList<string> EditRecipientLabels()
    {
        if (SelectedRule is null) return [];
        var specs = new List<RecipientSpec>();
        specs.AddRange(EditContextRoles.Select(r => new RecipientSpec(RecipientKind.ContextRole, r)));
        specs.AddRange(EditUserIds.Select(id => new RecipientSpec(RecipientKind.User, id.ToString())));
        specs.AddRange(EditGroupIds.Select(id => new RecipientSpec(RecipientKind.Group, id.ToString())));
        specs.AddRange(EditPermissions.Where(p => p.IsChecked)
            .Select(p => new RecipientSpec(RecipientKind.PermissionHolder, p.Item.ToString())));
        specs.AddRange(EditExternalAddresses
            .Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new RecipientSpec(RecipientKind.ExternalAddress, a)));
        return RecipientLabels(SelectedRule.EventType, specs);
    }

    private List<string> RecipientLabels(string eventType, IEnumerable<RecipientSpec> specs)
        => specs.Select(r => r.Kind switch
        {
            RecipientKind.ContextRole => RoleLabel(eventType, r.Value),
            RecipientKind.User or RecipientKind.Group
                => Guid.TryParse(r.Value, out var id) && _names.TryGetValue(id, out var name) ? name : r.Value,
            RecipientKind.PermissionHolder => string.Format(R("Web_Notifications_HoldersOf"), PermissionLabel(r.Value)),
            _ => r.Value,
        }).ToList();

    /// <summary>
    /// What a context role means for this particular event ("who created the upload link"),
    /// falling back to the generic role name.
    /// </summary>
    public static string RoleLabel(string eventType, string role)
        => Resources.ResourceManager.GetString($"Notification_Role_{eventType.Replace('.', '_')}_{role}")
           ?? ContextRoleLabel(role);

    public static string ContextRoleLabel(string role) => R("Web_Notifications_Role_" + role);

    private static string PermissionLabel(string value)
        => Enum.TryParse<ManagementPermission>(value, out var permission)
           && SelectablePermissions.FirstOrDefault(p => p.Permission == permission) is { Label: { } label }
            ? label
            : value;

    private async Task ReloadRulesAsync()
    {
        Rules = await repository.GetRulesAsync();
        LastSentByRule = await repository.GetLastSentPerRuleAsync();
    }

    private static IReadOnlyList<Guid> IdsOf(IEnumerable<RecipientSpec> specs, RecipientKind kind)
        => specs.Where(r => r.Kind == kind)
            .Select(r => Guid.TryParse(r.Value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToList();

    // ══════════════════════════════════════════
    //  Templates
    // ══════════════════════════════════════════

    private HashSet<(string Type, string Language)> _customTemplates = [];

    public string TemplateEventType { get; private set; } = NotificationEventCatalog.All[0].Type;
    public string TemplateLanguage { get; private set; } = "de";
    public string EditSubject { get; set; } = string.Empty;
    public string EditHtml { get; set; } = string.Empty;
    public string? TemplateError { get; private set; }
    public string PreviewSubject { get; private set; } = string.Empty;
    public string PreviewHtml { get; private set; } = string.Empty;

    public bool IsCustomized(string eventType, string language) => _customTemplates.Contains((eventType, language));
    public bool CurrentTemplateIsCustomized => IsCustomized(TemplateEventType, TemplateLanguage);
    public NotificationEventDefinition? CurrentDefinition => NotificationEventCatalog.Find(TemplateEventType);

    private async Task LoadTemplatesListAsync()
    {
        _customTemplates = (await repository.GetTemplatesAsync()).Select(t => (t.EventType, t.Language)).ToHashSet();
        await SelectTemplateAsync(TemplateEventType, _environment?.DefaultLanguage ?? "de");
    }

    public async Task SelectTemplateAsync(string eventType, string language)
    {
        if (NotificationEventCatalog.Find(eventType) is null) return;
        TemplateEventType = eventType;
        TemplateLanguage = DefaultMailTemplates.NormalizeLanguage(language);
        var source = await composer.GetTemplateAsync(TemplateEventType, TemplateLanguage);
        EditSubject = source.Subject;
        EditHtml = source.Html;
        await RefreshPreviewAsync();
    }

    /// <summary>Validates the edited sources and renders them with the catalog's sample values.</summary>
    public async Task RefreshPreviewAsync()
    {
        TemplateError = ValidateSources(EditSubject, EditHtml);
        if (TemplateError is not null || CurrentDefinition is not { } definition) return;
        try
        {
            var mail = await RenderSampleAsync(new MailTemplateSource(EditSubject, EditHtml), definition, TemplateLanguage);
            PreviewSubject = mail.Subject;
            PreviewHtml = mail.Html;
        }
        catch (Exception ex)
        {
            TemplateError = ex.Message;
        }
    }

    public async Task SaveTemplateAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        TemplateError = ValidateSources(EditSubject, EditHtml);
        if (TemplateError is not null) { ErrorMessage = R("Web_Notifications_TemplateInvalid"); return; }

        await MutateAsync(async () =>
        {
            await repository.SaveTemplateAsync(new MailTemplate
            {
                EventType = TemplateEventType,
                Language = TemplateLanguage,
                SubjectTemplate = EditSubject,
                HtmlTemplate = EditHtml,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = _actor?.User.Id,
            });
            _customTemplates.Add((TemplateEventType, TemplateLanguage));
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    public async Task ResetTemplateAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        await MutateAsync(async () =>
        {
            await repository.DeleteTemplateAsync(TemplateEventType, TemplateLanguage);
            _customTemplates.Remove((TemplateEventType, TemplateLanguage));
            await SelectTemplateAsync(TemplateEventType, TemplateLanguage);
            SuccessMessage = R("Web_Notifications_TemplateReset");
        });
    }

    /// <summary>Sends the edited (unsaved) template, rendered with sample values, to the current user.</summary>
    public async Task SendTemplateTestAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        if (!NotificationRecipientResolver.TryNormalizeAddress(_actor?.User.Email, out var to))
        {
            ErrorMessage = R("Web_Notifications_NoOwnEmail");
            return;
        }
        TemplateError = ValidateSources(EditSubject, EditHtml);
        if (TemplateError is not null || CurrentDefinition is not { } definition) return;

        try
        {
            var environment = await EnvironmentAsync();
            var mail = await RenderSampleAsync(new MailTemplateSource(EditSubject, EditHtml), definition, TemplateLanguage, forPreview: false);
            var result = await sender.TestAsync(await smtp.GetAsync(), null,
                NotificationMailComposer.ToMessage(environment, to, mail.Subject, mail.Html, mail.Text));
            if (result.Success) SuccessMessage = string.Format(R("Web_Notifications_TestSent"), to);
            else ErrorMessage = result.Error;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending a template test mail failed");
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Inserts <c>{{ name }}</c> at the end of the HTML source (the textarea keeps the caret in JS-free mode).</summary>
    public string PlaceholderToken(string name) => "{{ " + name + " }}";

    private static string? ValidateSources(string subject, string html)
    {
        var subjectError = MailTemplateRenderer.Validate(subject);
        if (subjectError is not null) return R("Web_Notifications_Subject") + ": " + subjectError;
        var htmlError = MailTemplateRenderer.Validate(html);
        return htmlError is null ? null : "HTML: " + htmlError;
    }

    private async Task<RenderedMail> RenderSampleAsync(
        MailTemplateSource source, NotificationEventDefinition definition, string language, bool forPreview = true)
    {
        var environment = (await EnvironmentAsync()) with { Layout = Layout };
        var sample = NotificationEventCatalog.SamplePayload(definition);
        return await composer.RenderAsync(source, environment, sample, language,
            sample["recipient.name"], sample["recipient.email"], DateTime.UtcNow, forPreview);
    }

    // ══════════════════════════════════════════
    //  Layout
    // ══════════════════════════════════════════

    /// <summary>The edited layout (also used by the template preview, so edits show up there).</summary>
    public MailLayoutSettings Layout { get; private set; } = MailLayoutSettings.Default();
    public string LayoutPreviewHtml { get; private set; } = string.Empty;
    public string? LayoutError { get; private set; }

    public async Task RefreshLayoutPreviewAsync()
    {
        LayoutError = MailTemplateRenderer.Validate(Layout.HeaderHtml) ?? MailTemplateRenderer.Validate(Layout.FooterHtml);
        if (LayoutError is not null) return;
        var definition = NotificationEventCatalog.Find(NotificationEventCatalog.UploadReceived)!;
        var language = _environment?.DefaultLanguage ?? "de";
        try
        {
            LayoutPreviewHtml = (await RenderSampleAsync(DefaultMailTemplates.Get(definition.Type, language), definition, language)).Html;
        }
        catch (Exception ex)
        {
            LayoutError = ex.Message;
        }
    }

    /// <summary>Stages an uploaded logo; returns an error text or null.</summary>
    public string? SetLogo(byte[] bytes, string contentType)
    {
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif"))
            return R("Web_Notifications_LogoType");
        if (bytes.Length > MailLayoutSettings.MaxLogoBytes)
            return string.Format(R("Web_Notifications_LogoTooLarge"), MailLayoutSettings.MaxLogoBytes / 1024);
        Layout.LogoDataUri = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        return null;
    }

    public void ClearLogo() => Layout.LogoDataUri = null;

    public async Task SaveLayoutAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        LayoutError = MailTemplateRenderer.Validate(Layout.HeaderHtml) ?? MailTemplateRenderer.Validate(Layout.FooterHtml);
        if (LayoutError is not null) { ErrorMessage = R("Web_Notifications_TemplateInvalid"); return; }

        var toSave = Layout with { };
        toSave.Normalize();
        await MutateAsync(async () =>
        {
            await config.SetAsync(MailLayoutSettings.ConfigKey, toSave);
            _environment = await composer.LoadEnvironmentAsync();
            Layout = _environment.Layout with { };
            await RefreshLayoutPreviewAsync();
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    // ══════════════════════════════════════════
    //  Delivery log
    // ══════════════════════════════════════════

    public List<MailDelivery> Deliveries { get; private set; } = [];
    public MailDelivery? ViewedDelivery { get; private set; }

    public async Task RefreshLogAsync()
    {
        Deliveries = await repository.GetRecentDeliveriesAsync(LogSize);
        PendingCount = await repository.CountPendingDeliveriesAsync();
    }

    /// <summary>Opens the rendered mail. The body holds user data, so it is re-checked here.</summary>
    public async Task ViewDeliveryAsync(Guid id)
    {
        ViewedDelivery = await HasPermissionAsync() ? await repository.GetDeliveryAsync(id) : null;
    }

    public void CloseDelivery() => ViewedDelivery = null;

    /// <summary>Queues a failed or dead delivery for an immediate new attempt.</summary>
    public async Task RetryDeliveryAsync(Guid id)
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }
        var delivery = await repository.GetDeliveryAsync(id);
        if (delivery is null || delivery.Status is not (MailDeliveryStatus.Dead or MailDeliveryStatus.Failed)) return;

        delivery.Status = MailDeliveryStatus.Pending;
        delivery.AttemptCount = 0;
        delivery.NextAttemptUtc = DateTime.UtcNow;
        await MutateAsync(async () =>
        {
            await repository.UpdateDeliveryAsync(delivery);
            signal.Wake();
            await RefreshLogAsync();
            SuccessMessage = R("Web_Notifications_RetryQueued");
        });
    }

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    private async Task MutateAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notification settings change failed");
            ErrorMessage = R("Web_Error_SaveFailed");
        }
    }

    private async Task<MailEnvironment> EnvironmentAsync()
        => _environment ??= await composer.LoadEnvironmentAsync();

    private async Task<bool> HasPermissionAsync()
        => _actor is not null
           && (await mgmtAuth.GetEffectivePermissionsAtAsync(_actor, ScopeType.Global, Guid.Empty))
               .HasFlag(ManagementPermission.ManageNotifications);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    public static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
