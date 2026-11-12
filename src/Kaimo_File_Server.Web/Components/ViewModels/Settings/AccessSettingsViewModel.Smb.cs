using Kaimo_File_Server.Core.Security.Authentication;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// SMB card of the "Access restrictions" tab: the client networks Samba accepts for every user
/// (<see cref="AccessRestrictionSettings.SmbAllowedNetworks"/>, applied as <c>hosts allow</c> by
/// the Samba config sync). There is no self-lockout check: the admin edits this over the web UI,
/// which the list does not affect.
/// </summary>
public sealed partial class AccessSettingsViewModel
{
    private List<string> _smbAllowedNetworks = new();

    /// <summary>The network list in the editor, one CIDR or address per line; empty allows every network.</summary>
    public string SmbNetworks { get; set; } = string.Empty;

    private void LoadSmbNetworks(AccessRestrictionSettings settings)
    {
        _smbAllowedNetworks = settings.SmbAllowedNetworks.ToList();
        SmbNetworks = string.Join(Environment.NewLine, _smbAllowedNetworks);
    }

    public async Task SaveSmbNetworksAsync()
    {
        ClearMessages();
        if (!await HasPermissionAsync()) { ErrorMessage = R("Web_Settings_NoPermissionChange"); return; }

        var lines = SmbNetworks.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = lines.Where(l => !AccessRestrictionSettings.TryParseNetwork(l, out _)).ToList();
        if (invalid.Count > 0)
        {
            ErrorMessage = string.Format(R("Web_AccessRestrictions_Error_Network"), string.Join(", ", invalid));
            return;
        }
        if (lines.Length > AccessRestrictionSettings.MaxSmbAllowedNetworks)
        {
            ErrorMessage = string.Format(R("Web_AccessRestrictions_Error_TooManySmbNetworks"), AccessRestrictionSettings.MaxSmbAllowedNetworks);
            return;
        }
        var networks = new AccessRestrictionSettings { SmbAllowedNetworks = lines.ToList() }.SmbHostsAllow().ToList();

        await SaveAsync(async () =>
        {
            // Keep the rules as stored now, not as this tab loaded them (another admin may have
            // changed them meanwhile), and show them, so this tab's next rule save keeps them too.
            var rules = (await access.GetSettingsAsync()).Rules.Select(r => r.Clone()).ToList();
            await access.SaveSettingsAsync(new AccessRestrictionSettings
            {
                Rules = rules.Select(r => r.Clone()).ToList(),
                SmbAllowedNetworks = networks,
            });
            Rules = rules;
            LoadSmbNetworks(new AccessRestrictionSettings { SmbAllowedNetworks = networks });
            logger.LogInformation("SMB allowed networks saved ({Count} networks)", networks.Count);
            SuccessMessage = R("Web_ChangesSaved");
        });
    }
}
