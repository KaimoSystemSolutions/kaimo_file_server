using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the "Domains" and "Access restrictions" settings tabs: the central list of server
/// addresses and the rules that limit where users, groups or departments may sign in from.
/// Gated by <see cref="ManagementPermission.ManageSystemSettings"/>, re-checked on every
/// mutation. A save that would block the admin's own current connection is refused.
/// </summary>
public sealed class AccessSettingsViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    IConfigRepository config,
    AccessRestrictionService access,
    IUserRepository users,
    IGroupRepository groups,
    IDepartmentRepository departments,
    ClientConnectionInfo client,
    ILogger<AccessSettingsViewModel> logger)
{
    private UserContext? _actor;
    private bool _loaded;

    public bool IsLoading { get; private set; } = true;
    public bool CanManage { get; private set; }
    public bool IsSaving { get; private set; }
    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>Address and host of the admin's own connection (shown, and used for the lockout check).</summary>
    public string ClientAddress => client.RemoteAddress ?? "–";
    public string ClientHost => client.Host ?? "–";

    // ── Domains ──

    public List<string> Addresses { get; private set; } = new();
    public int DefaultIndex { get; set; }

    // ── Rules ──

    public List<AccessRule> Rules { get; private set; } = new();
    public IReadOnlyList<PrincipalPickerItem> UserItems { get; private set; } = [];
    public IReadOnlyList<PrincipalPickerItem> GroupItems { get; private set; } = [];
    public IReadOnlyList<PrincipalPickerItem> DepartmentItems { get; private set; } = [];

    /// <summary>The rule in the editor (a copy; null when the editor is closed).</summary>
    public AccessRule? Editing { get; private set; }
    public bool IsNew { get; private set; }
    public List<CheckboxItem<string>> EditHosts { get; private set; } = new();
    public string EditNetworks { get; set; } = string.Empty;

    /// <summary>The rule awaiting delete confirmation.</summary>
    public AccessRule? PendingDelete { get; set; }

    /// <summary>Loads everything once per circuit; both tabs share the instance.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        IsLoading = true;
        try
        {
            _actor = await GetActorAsync();
            CanManage = await HasPermissionAsync();
            if (!CanManage) return;

            await LoadDomainsAsync();
            Rules = (await access.GetSettingsAsync()).Rules.Select(r => r.Clone()).ToList();

            UserItems = (await users.GetAllAsync()).OrderBy(u => u.Name)
                .Select(u => new PrincipalPickerItem(u.Id, u.Name, PrincipalKind.User,
                    string.IsNullOrWhiteSpace(u.Email) ? u.Username : u.Email))
                .ToList();
            GroupItems = (await groups.GetAllAsync()).OrderBy(g => g.Name)
                .Select(g => new PrincipalPickerItem(g.Id, g.Name, PrincipalKind.Group))
                .ToList();
            DepartmentItems = (await departments.GetAllAsync()).OrderBy(d => d.Name).ToDepartmentItems();
            _loaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the domain / access restriction settings");
            ErrorMessage = R("Web_Settings_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearMessages()
    {
        SuccessMessage = null;
        ErrorMessage = null;
    }

    // ══════════════════════════════════════════
    //  Domains
    // ══════════════════════════════════════════

    public void AddAddress()
    {
        if (Addresses.Count < ServerDomainSettings.MaxAddresses)
            Addresses.Add(string.Empty);
    }

    public void RemoveAddress(int index)
    {
        Addresses.RemoveAt(index);
        if (DefaultIndex >= Addresses.Count)
            DefaultIndex = Math.Max(0, Addresses.Count - 1);
    }

    /// <summary>
    /// Saves the address list. Hosts of removed addresses are also dropped from the rules, so a
    /// rule never keeps allowing an address that is no longer listed. Refused when that would
    /// leave a rule without any domain or IP range.
    /// </summary>
    public async Task SaveDomainsAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        var invalid = Addresses.Select(a => a.Trim()).Where(a => a.Length > 0 && !ServerDomainSettings.IsValidAddress(a.TrimEnd('/'))).ToList();
        if (invalid.Count > 0)
        {
            ErrorMessage = string.Format(R("Web_DomainSettings_Invalid"), string.Join(", ", invalid));
            return;
        }

        var domains = new ServerDomainSettings { Addresses = Addresses.ToList(), DefaultIndex = DefaultIndex };
        domains.Normalize();

        var hosts = domains.Addresses.Select(ServerDomainSettings.HostOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pruned = Rules.Select(r => r.Clone()).ToList();
        foreach (var rule in pruned)
            rule.Hosts.RemoveAll(h => !hosts.Contains(h));
        var rulesChanged = pruned.Zip(Rules).Any(p => p.First.Hosts.Count != p.Second.Hosts.Count);

        // A rule left without any domain or IP range would block every user it covers.
        var emptied = pruned.Where(r => r.Hosts.Count + r.Networks.Count == 0).Select(r => r.Name).ToList();
        if (emptied.Count > 0)
        {
            ErrorMessage = string.Format(R("Web_DomainSettings_Error_RuleWithoutSource"), string.Join(", ", emptied));
            return;
        }

        await SaveAsync(async () =>
        {
            if (rulesChanged && !await SaveRulesAsync(pruned)) return;
            await config.SetAsync(ServerDomainSettings.ConfigKey, domains);
            await LoadDomainsAsync();
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    private async Task LoadDomainsAsync()
    {
        var domains = await ServerDomainSettings.LoadAsync(config);
        Addresses = domains.Addresses.ToList();
        DefaultIndex = domains.DefaultIndex;
    }

    // ══════════════════════════════════════════
    //  Rules
    // ══════════════════════════════════════════

    public void NewRule() => OpenEditor(new AccessRule(), isNew: true);

    public void EditRule(AccessRule rule) => OpenEditor(rule.Clone(), isNew: false);

    public void CancelEdit()
    {
        Editing = null;
        ClearMessages();
    }

    private void OpenEditor(AccessRule rule, bool isNew)
    {
        ClearMessages();
        Editing = rule;
        IsNew = isNew;
        // Only listed addresses can be picked; hosts no longer listed are dropped on save.
        EditHosts = Addresses
            .Select(ServerDomainSettings.HostOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => new CheckboxItem<string>(h, rule.Hosts.Contains(h, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        EditNetworks = string.Join(Environment.NewLine, rule.Networks);
    }

    public async Task SaveRuleAsync()
    {
        ClearMessages();
        if (Editing is null) return;
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        var rule = Editing.Clone();
        rule.Name = rule.Name.Trim();
        rule.Hosts = EditHosts.Where(h => h.IsChecked).Select(h => h.Item).ToList();

        var lines = EditNetworks.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = lines.Where(l => !AccessRestrictionSettings.TryParseNetwork(l, out _)).ToList();
        if (invalid.Count > 0)
        {
            ErrorMessage = string.Format(R("Web_AccessRestrictions_Error_Network"), string.Join(", ", invalid));
            return;
        }
        rule.Networks = lines
            .Select(l => { AccessRestrictionSettings.TryParseNetwork(l, out var n); return n.ToString(); })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rule.Name.Length == 0) { ErrorMessage = R("Web_AccessRestrictions_Error_Name"); return; }
        if (rule.UserIds.Count + rule.GroupIds.Count + rule.DepartmentIds.Count == 0)
        {
            ErrorMessage = R("Web_AccessRestrictions_Error_Principals");
            return;
        }
        if (rule.Hosts.Count + rule.Networks.Count == 0) { ErrorMessage = R("Web_AccessRestrictions_Error_Sources"); return; }

        var candidate = Rules.Select(r => r.Id == rule.Id ? rule : r.Clone()).ToList();
        if (IsNew) candidate.Add(rule);

        await SaveAsync(async () =>
        {
            if (!await SaveRulesAsync(candidate)) return;
            Editing = null;
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    public async Task DeleteRuleAsync()
    {
        ClearMessages();
        var target = PendingDelete;
        PendingDelete = null;
        if (target is null) return;
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        await SaveAsync(async () =>
        {
            if (!await SaveRulesAsync(Rules.Where(r => r.Id != target.Id).Select(r => r.Clone()).ToList())) return;
            if (Editing?.Id == target.Id) Editing = null;
            SuccessMessage = R("Web_ChangesSaved");
        });
    }

    /// <summary>"3 principals · 1 domain · 2 IP ranges" for the rule list.</summary>
    public static string Summary(AccessRule rule) => string.Format(
        R("Web_AccessRestrictions_Summary"),
        rule.UserIds.Count + rule.GroupIds.Count + rule.DepartmentIds.Count,
        rule.Hosts.Count,
        rule.Networks.Count);

    /// <summary>Persists <paramref name="rules"/> unless they would block the admin's own connection.</summary>
    private async Task<bool> SaveRulesAsync(List<AccessRule> rules)
    {
        var candidate = new AccessRestrictionSettings { Rules = rules };
        if (_actor is null || !await access.IsAllowedAsync(_actor, client.RemoteAddress, client.Host, candidate))
        {
            ErrorMessage = string.Format(R("Web_AccessRestrictions_Error_SelfLockout"), ClientAddress, ClientHost);
            return false;
        }

        await access.SaveSettingsAsync(candidate);
        Rules = rules.Select(r => r.Clone()).ToList();
        logger.LogInformation("Access restriction rules saved ({Count} rules)", rules.Count);
        return true;
    }

    private async Task SaveAsync(Func<Task> save)
    {
        IsSaving = true;
        try
        {
            await save();
        }
        catch (ReadOnlyDemoException)
        {
            ErrorMessage = R("Web_Demo_ReadOnlyNotice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save the domain / access restriction settings");
            ErrorMessage = R("Web_Error_SaveFailed");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task<bool> HasPermissionAsync()
        => _actor is not null
           && (await mgmtAuth.GetEffectivePermissionsAtAsync(_actor, ScopeType.Global, Guid.Empty))
               .HasFlag(ManagementPermission.ManageSystemSettings);

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrEmpty(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
