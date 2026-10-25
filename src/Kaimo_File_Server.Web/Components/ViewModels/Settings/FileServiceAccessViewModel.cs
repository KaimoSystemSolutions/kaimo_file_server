using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Backs the per-user file-service matrix on the "File services" settings tab: which user
/// may use the client app, the web login, SMB and WebDAV, plus the default applied to newly
/// created users. Every change is saved at once. Members of the Admins group always keep the
/// web login (see <see cref="ServiceAccessGate"/>), so their web cell is not editable.
/// Requires global <see cref="ManagementPermission.EnableDisableUsers"/>.
/// </summary>
public sealed class FileServiceAccessViewModel(
    AuthenticationStateProvider authState,
    IUserContextFactory userContextFactory,
    IManagementAuthService mgmtAuth,
    IUserRepository users,
    IGroupRepository groups,
    IConfigRepository config,
    ILogger<FileServiceAccessViewModel> logger,
    IMemoryCache? cache = null)
{
    /// <summary>The switchable services in display order.</summary>
    public static readonly UserServiceAccess[] Services =
        [UserServiceAccess.App, UserServiceAccess.Web, UserServiceAccess.Smb, UserServiceAccess.WebDav];

    /// <summary>One user row; <see cref="Access"/> reflects what is stored.</summary>
    public sealed class Row(User user, bool isAdmin)
    {
        public User User { get; } = user;
        public Guid Id { get; } = user.Id;
        public string Name { get; } = user.Name;
        public string Username { get; } = user.Username;
        public bool IsAdmin { get; } = isAdmin;
        public UserServiceAccess Access { get; set; } = user.ServiceAccess;

        /// <summary>Whether the cell is locked on: the web login of an administrator.</summary>
        public bool IsLocked(UserServiceAccess service) => IsAdmin && service == UserServiceAccess.Web;

        public bool Has(UserServiceAccess service) => IsLocked(service) || Access.HasFlag(service);
    }

    public bool IsLoading { get; private set; } = true;
    public bool LoadFailed { get; private set; }
    public bool CanManage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public UserServiceAccess DefaultAccess { get; private set; } = UserServiceAccess.All;

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadFailed = false;
        ErrorMessage = null;
        try
        {
            var actor = await GetCurrentUserAsync();
            CanManage = actor is not null
                && await mgmtAuth.HasGlobalPermissionAsync(actor, ManagementPermission.EnableDisableUsers);
            if (!CanManage)
            {
                Rows = [];
                return;
            }

            var adminIds = (await groups.GetMembersAsync(WellKnownGUIDs.GROUP_ADMINS)).Select(u => u.Id).ToHashSet();
            Rows = (await users.GetAllAsync())
                .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(u => new Row(u, adminIds.Contains(u.Id)))
                .ToList();
            DefaultAccess = (UserServiceAccess)await config.GetIntAsync(
                ServiceAccessGate.DefaultAccessKey, (int)UserServiceAccess.All) & UserServiceAccess.All;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading the per-user file services failed");
            LoadFailed = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Users that may use <paramref name="service"/> by their own switch (admins always count for web).</summary>
    public int CountAllowed(UserServiceAccess service) => Rows.Count(r => r.Has(service));

    /// <summary>Rows whose name or username contains <paramref name="filter"/>.</summary>
    public IEnumerable<Row> Filter(string? filter) => string.IsNullOrWhiteSpace(filter)
        ? Rows
        : Rows.Where(r => r.Name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase)
                       || r.Username.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Switches one service for one user and saves it; reverts the row on failure.</summary>
    public async Task SetAsync(Row row, UserServiceAccess service, bool allowed)
    {
        ErrorMessage = null;
        if (!CanManage || row.IsLocked(service)) return;

        var previous = row.Access;
        row.Access = allowed ? previous | service : previous & ~service;
        try
        {
            await users.UpdateServiceAccessAsync(row.Id, row.Access);
            // Cached WebDAV Basic logins would otherwise outlive the change for up to a minute.
            if (cache is not null)
                WebDavBasicAuthenticationHandler.Invalidate(cache, row.Id);
            logger.LogInformation("File service {Service} set to {Allowed} for user {User}", service, allowed, row.Username);
        }
        catch (Exception ex)
        {
            row.Access = previous;
            logger.LogError(ex, "Saving file service {Service} for user {User} failed", service, row.Username);
            ErrorMessage = Resources.ResourceManager.GetString("Web_FileServices_SaveFailed");
        }
    }

    /// <summary>Changes the default for newly created users; existing users stay as they are.</summary>
    public async Task SetDefaultAsync(UserServiceAccess service, bool allowed)
    {
        ErrorMessage = null;
        if (!CanManage) return;

        var previous = DefaultAccess;
        DefaultAccess = allowed ? previous | service : previous & ~service;
        try
        {
            await config.SetAsync(ServiceAccessGate.DefaultAccessKey, (int)DefaultAccess);
        }
        catch (Exception ex)
        {
            DefaultAccess = previous;
            logger.LogError(ex, "Saving the default file services failed");
            ErrorMessage = Resources.ResourceManager.GetString("Web_FileServices_SaveFailed");
        }
    }

    private async Task<UserContext?> GetCurrentUserAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await userContextFactory.CreateByUsernameAsync(username);
    }
}
