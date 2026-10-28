using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// One searchable navigation target (a tab or a settings section) shown in the
/// global search alongside file hits.
///
/// <para><see cref="TitleKey"/> is a resource key resolved in the current UI
/// culture, so the localized name matches in both languages. <see cref="Keywords"/>
/// are extra, clearly-assigned buzzwords (German + English) — each concept lives
/// on exactly one destination so a term never resolves ambiguously.</para>
///
/// <para><see cref="RequiredAny"/> is the access gate: <c>null</c> means always
/// visible; otherwise the user must hold ANY of the flagged management permissions
/// (checked via <see cref="IManagementAuthService.HasAnyPermissionAsync"/>, which
/// honors scoped assignments too; for <see cref="GlobalOnly"/> destinations against the
/// global mask, because their page checks the bits at global scope only). It mirrors the per-page/per-tab gates so search never reveals a
/// destination the user cannot open.</para>
/// </summary>
public sealed record SearchDestination(
    string TitleKey,
    string[] Keywords,
    string Url,
    string Icon,
    ManagementPermission? RequiredAny,
    bool GlobalOnly = false)
{
    public string Title => Resources.ResourceManager.GetString(TitleKey) ?? TitleKey;

    /// <summary>
    /// A short area label shown as the result's subtitle (like the share name on a
    /// file hit), derived from the route so entries don't have to carry it.
    /// </summary>
    public string Category => Url switch
    {
        _ when Url.StartsWith("/settings", StringComparison.Ordinal) => Loc("Web_Nav_Settings", "Settings"),
        _ when Url.StartsWith("/external-storage", StringComparison.Ordinal)
            => Loc("Web_ExternalStorage_Title", "External Storage"),
        _ when Url.StartsWith("/shares?view=virtual", StringComparison.Ordinal) => Loc("Web_Nav_Shares", "Shares"),
        _ => Loc("Web_Search_Cat_Page", "Page"),
    };

    private static string Loc(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;
}

/// <summary>
/// The single registry of searchable tabs/settings. There is no central navigation
/// registry elsewhere in the app, so this is the one place that enumerates
/// destinations with their keywords, routes and access gates.
/// </summary>
public static class SearchDestinations
{
    // Gates copied from the pages/VMs they guard so search visibility == page access:
    //  • Users      → UserListViewModel.CanAccessPage
    //  • Departments→ DepartmentViewModel.CanAccessPage
    //  • Settings   → SettingsViewModel per-tab flags
    private const ManagementPermission UsersGate =
        ManagementPermission.CreateUsers | ManagementPermission.EditUserProfiles
        | ManagementPermission.CreateGroups | ManagementPermission.ManageGroupMembers
        | ManagementPermission.AssignRoles;

    private const ManagementPermission DepartmentsGate =
        ManagementPermission.ViewDepartment | ManagementPermission.EditDepartment;

    private const ManagementPermission ConnectionsGate =
        ManagementPermission.ManageConnections | ManagementPermission.UseConnections
        | ManagementPermission.ManageCloudAccess;

    private const ManagementPermission LoggingGate =
        ManagementPermission.ManageSystemSettings | ManagementPermission.ViewSystemLogs;

    private const ManagementPermission Settings = ManagementPermission.ManageSystemSettings;

    /// <summary>
    /// A destination whose page checks its bits at global scope only (the settings tabs via
    /// <c>SettingsViewModel</c>, the notifications page via <c>NotificationsViewModel</c>).
    /// </summary>
    private static SearchDestination Global(string titleKey, string[] keywords, string url, string icon, ManagementPermission requiredAny)
        => new(titleKey, keywords, url, icon, requiredAny, GlobalOnly: true);

    public static readonly IReadOnlyList<SearchDestination> All = new List<SearchDestination>
    {
        // ── Top-level tabs ──
        new("Web_Nav_Home",
            ["home", "start", "startseite", "overview", "übersicht", "dashboard"],
            "/", "home.svg", null),
        new("Web_Nav_Shares",
            ["shares", "share", "freigaben", "freigabe", "dateifreigabe"],
            "/shares", "folder.svg", null),
        new("Web_Nav_BrowseFiles",
            ["files", "dateien", "browse", "durchsuchen", "explorer", "dateibrowser"],
            "/files", "compass.svg", null),
        new("Web_Nav_Users",
            ["users", "user", "benutzer", "nutzer", "konten", "accounts", "groups", "gruppen", "roles", "rollen"],
            "/users", "users.svg", UsersGate),
        new("Web_Nav_Departments",
            ["departments", "department", "abteilungen", "abteilung", "organisation"],
            "/departments", "building.svg", DepartmentsGate),

        // ── External storage / sync / virtual shares ──
        new("Web_ExternalStorage_Tab_Syncs",
            ["sync", "syncs", "synchronisation", "synchronisierung"],
            "/external-storage?tab=syncs", "cloud-download.svg", ManagementPermission.SyncAdmin),
        new("Web_ExternalStorage_Tab_Connections",
            ["connection", "connections", "verbindung", "verbindungen", "external storage", "externer speicher"],
            "/external-storage?tab=connections", "cloud-download.svg", ConnectionsGate),
        new("Web_Shares_VirtualShares",
            ["cloud access", "virtual shares", "virtuelle shares", "virtueller share", "remote shares"],
            "/shares?view=virtual", "cloud-download.svg", ConnectionsGate),

        // ── Settings sections (deep-linked via ?tab=) ──
        Global("Web_Settings_Tab_Region",
            ["language", "sprache", "localization", "lokalisierung", "i18n",
             "region", "date format", "datumsformat", "datum", "date", "iso"],
            "/settings?tab=language", "globe.svg", Settings),
        Global("Web_Settings_Tab_ContextMenu",
            ["context menu", "kontextmenü", "kontextmenu", "rechtsklick"],
            "/settings?tab=contextmenu", "edit.svg", Settings),
        Global("Web_Settings_Tab_Password",
            ["password", "passwort", "kennwort", "passwortrichtlinie", "password policy"],
            "/settings?tab=passwordpolicy", "shield.svg", Settings),
        Global("Web_Settings_Tab_SystemStatus",
            ["system status", "systemstatus", "status", "network", "netzwerk", "ip", "ip-adresse", "ip address",
             "ram", "memory", "arbeitsspeicher", "hostname"],
            "/settings?tab=systemstatus", "compass.svg", Settings),
        Global("Web_Settings_Tab_Storage",
            ["storage", "speicher", "speicherpool", "pool", "festplatte", "disk"],
            "/settings?tab=storage", "folder.svg", Settings),
        Global("Web_Settings_Tab_Versioning",
            ["versioning", "versionierung", "versions", "versionen", "version history", "versionsverlauf",
             "snapshot", "snapshots", "retention", "aufbewahrung"],
            "/settings?tab=versioning", "history.svg", Settings),
        // "users"/"user" deliberately overlap with the Users page: the share is named
        // "users", its web label "user".
        new("Web_Nav_HomeFolders",
            ["home", "homes", "users", "user", "home folder", "home-ordner", "heimverzeichnis", "benutzerordner"],
            "/home-folders", "user.svg", ManagementPermission.ManageHomes),
        Global("Web_Nav_HomeFolders",
            ["home", "homes", "home folder", "home-ordner", "heimverzeichnis", "benutzerordner",
             "home folder settings", "home-ordner einstellungen"],
            "/settings?tab=homefolders", "user.svg", Settings),
        Global("Web_Recycle_SectionLabel",
            ["recycle bin", "papierkorb", "trash", "retention", "aufbewahrung"],
            "/settings?tab=storage", "folder.svg", Settings),
        Global("Web_Settings_Tab_Search",
            ["search", "suche", "elasticsearch", "index", "indexierung", "suchmaschine"],
            "/settings?tab=search", "search.svg", Settings),
        Global("Web_Settings_Tab_CloudAccess",
            ["cache", "directory cache", "verzeichnis-cache", "cloud access cache", "cache ttl", "cloudaccess cache"],
            "/settings?tab=cloudaccess", "cloud-download.svg", Settings),
        Global("Web_Settings_Tab_Domains",
            ["domain", "domains", "domäne", "domänen", "address", "adresse", "public url", "öffentliche adresse", "base url"],
            "/settings?tab=domains", "globe.svg", Settings),
        Global("Web_Settings_Tab_AccessRestrictions",
            ["access restriction", "zugriffsbeschränkung", "ip range", "ip-bereich", "allowlist", "whitelist", "cidr"],
            "/settings?tab=accessrestrictions", "shield.svg", Settings),
        Global("Web_Settings_Tab_Logging",
            ["logging", "logs", "protokoll", "protokollierung", "log level"],
            "/settings?tab=logging", "gear.svg", LoggingGate),
        Global("Web_Settings_Tab_Fileservices",
            ["file services", "dateidienste", "datendienste", "smb", "samba", "nfs", "ftp"],
            "/settings?tab=dataservices", "archive.svg", ManagementPermission.ManageDataServices),
        Global("Web_Settings_Tab_Certificate",
            ["certificate", "zertifikat", "https", "ssl", "tls"],
            "/settings?tab=certificate", "shield.svg", ManagementPermission.ManageCertificates),
        Global("Web_Settings_Tab_Backup",
            ["backup", "sicherung", "datensicherung", "database backup", "datenbank-sicherung", "datenbank"],
            "/settings?tab=backup", "archive.svg", ManagementPermission.ManageBackups),
        new("Web_Nav_Backup",
            ["file backup", "dateisicherung", "restic", "restore", "wiederherstellung", "backup repository", "recovery kit"],
            "/backup", "archive.svg", ManagementPermission.ManageBackupRepositories | ManagementPermission.ManageBackupJobs
                                      | ManagementPermission.RestoreFromBackup | ManagementPermission.RestoreBackupInPlace),
        Global("Web_Nav_DeviceAdmin",
            ["client devices", "geräte", "clients", "device", "mobile app"],
            "/settings?tab=clientdevices", "refresh.svg", ManagementPermission.ManageClientDevices),
        new("Web_Nav_Security",
            ["security", "sicherheit", "login attempts", "anmeldeversuche", "lockout", "sperre", "api requests"],
            "/security", "shield.svg", ManagementPermission.ViewSecurityMonitor),
        Global("Web_Nav_Notifications",
            ["notifications", "benachrichtigungen", "mail", "e-mail", "email templates", "mailvorlagen", "rules", "regeln"],
            "/notifications", "mail.svg", ManagementPermission.ManageNotifications),
        Global("Web_Settings_Tab_MailServer",
            ["mail server", "mailserver", "smtp", "e-mail", "email", "test mail", "testmail"],
            "/settings?tab=mailserver", "mail.svg", ManagementPermission.ManageMailServer),
    };

    /// <summary>
    /// Returns the destinations matching <paramref name="query"/> that the user may
    /// access. Matching is a case-insensitive substring test against each keyword and
    /// the localized title. Registry order is preserved (the intended ranking).
    /// Access is checked only for the few keyword matches, so this stays cheap.
    /// </summary>
    public static async Task<List<SearchDestination>> MatchAsync(
        string query, IManagementAuthService mgmt, UserContext user, CancellationToken ct = default)
    {
        // A leading "/" (path-style input such as "/homes") is ignored for destinations.
        var q = (query ?? string.Empty).Trim().TrimStart('/');
        if (q.Length < 2 || user is null)
            return new List<SearchDestination>();

        var matched = All.Where(d => Matches(d, q)).ToList();

        var allowed = new List<SearchDestination>(matched.Count);
        ManagementPermission? globalMask = null;
        foreach (var d in matched)
        {
            ct.ThrowIfCancellationRequested();
            if (d.RequiredAny is not { } gate)
                allowed.Add(d);
            else if (d.GlobalOnly)
            {
                globalMask ??= await mgmt.GetEffectivePermissionsAtAsync(user, ScopeType.Global, Guid.Empty);
                if ((globalMask.Value & gate) != 0)
                    allowed.Add(d);
            }
            else if (await mgmt.HasAnyPermissionAsync(user, gate))
                allowed.Add(d);
        }
        return allowed;
    }

    private static bool Matches(SearchDestination d, string q)
    {
        if (d.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var kw in d.Keywords)
            if (kw.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
