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
/// honors scoped assignments too). It mirrors the per-page/per-tab gates so search
/// never reveals a destination the user cannot open.</para>
/// </summary>
public sealed record SearchDestination(
    string TitleKey,
    string[] Keywords,
    string Url,
    string Icon,
    ManagementPermission? RequiredAny)
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
            || Url.StartsWith("/sync", StringComparison.Ordinal)
            || Url.StartsWith("/cloud-access", StringComparison.Ordinal)
            => Loc("Web_ExternalStorage_Title", "External Storage"),
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

    public static readonly IReadOnlyList<SearchDestination> All = new List<SearchDestination>
    {
        // ── Top-level tabs ──
        new("Web_Nav_Shares",
            ["shares", "share", "freigaben", "freigabe", "dateifreigabe"],
            "/", "folder.svg", null),
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
        new("Web_CloudSync_Title",
            ["cloud sync", "cloudsync", "ordner sync", "folder sync"],
            "/sync", "cloud-download.svg", ManagementPermission.SyncAdmin),
        new("Web_ExternalStorage_Tab_Connections",
            ["connection", "connections", "verbindung", "verbindungen", "external storage", "externer speicher"],
            "/external-storage?tab=connections", "cloud-download.svg", ConnectionsGate),
        new("Web_CloudAccess_Title",
            ["cloud access", "virtual shares", "virtuelle shares", "virtueller share", "remote shares"],
            "/cloud-access", "cloud-download.svg", ConnectionsGate),

        // ── Settings sections (deep-linked via ?tab=) ──
        new("Web_Settings_Tab_Language",
            ["language", "sprache", "localization", "lokalisierung", "i18n"],
            "/settings?tab=language", "globe.svg", Settings),
        new("Web_Settings_Tab_ContextMenu",
            ["context menu", "kontextmenü", "kontextmenu", "rechtsklick"],
            "/settings?tab=contextmenu", "edit.svg", Settings),
        new("Web_Settings_Tab_Password",
            ["password", "passwort", "kennwort", "passwortrichtlinie", "password policy"],
            "/settings?tab=passwordpolicy", "shield.svg", Settings),
        new("Web_Settings_Tab_IP_Address",
            ["network", "netzwerk", "ip", "ip-adresse", "ip address"],
            "/settings?tab=network", "compass.svg", Settings),
        new("Web_Settings_Tab_Storage",
            ["storage", "speicher", "speicherpool", "pool", "festplatte", "disk"],
            "/settings?tab=storage", "folder.svg", Settings),
        new("Web_Settings_Tab_Ram",
            ["ram", "memory", "arbeitsspeicher"],
            "/settings?tab=memory", "gear.svg", Settings),
        new("Web_Settings_Tab_Search",
            ["search", "suche", "elasticsearch", "index", "indexierung", "suchmaschine"],
            "/settings?tab=search", "search.svg", Settings),
        new("Web_Settings_Tab_CloudAccess",
            ["cloud access cache", "cache ttl", "cloudaccess cache"],
            "/settings?tab=cloudaccess", "cloud-download.svg", Settings),
        new("Web_Settings_Tab_Logging",
            ["logging", "logs", "protokoll", "protokollierung", "log level"],
            "/settings?tab=logging", "gear.svg", LoggingGate),
        new("Web_Settings_Tab_Fileservices",
            ["file services", "dateidienste", "datendienste", "smb", "samba", "nfs", "ftp"],
            "/settings?tab=dataservices", "archive.svg", ManagementPermission.ManageDataServices),
        new("Web_Settings_Tab_Certificate",
            ["certificate", "zertifikat", "https", "ssl", "tls"],
            "/settings?tab=certificate", "shield.svg", ManagementPermission.ManageCertificates),
        new("Web_Settings_Tab_Backup",
            ["backup", "sicherung", "datensicherung", "database backup"],
            "/settings?tab=backup", "archive.svg", ManagementPermission.ManageBackups),
        new("Web_Nav_DeviceAdmin",
            ["client devices", "geräte", "clients", "device", "mobile app"],
            "/settings?tab=clientdevices", "refresh.svg", ManagementPermission.ManageClientDevices),
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
        var q = (query ?? string.Empty).Trim();
        if (q.Length < 2 || user is null)
            return new List<SearchDestination>();

        var matched = All.Where(d => Matches(d, q)).ToList();

        var allowed = new List<SearchDestination>(matched.Count);
        foreach (var d in matched)
        {
            ct.ThrowIfCancellationRequested();
            if (d.RequiredAny is null
                || await mgmt.HasAnyPermissionAsync(user, d.RequiredAny.Value))
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
