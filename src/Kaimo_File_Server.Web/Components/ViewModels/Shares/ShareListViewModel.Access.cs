using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class ShareListViewModel
{
    // -- Access --

    public bool ShowAccessPanel { get; set; }
    public List<User> AllUsers { get; private set; } = [];
    public List<Core.Domain.Identity.Group> AllGroups { get; private set; } = [];
    public string? AccessErrorMessage { get; private set; }

    // -- Access Management --

    public async Task LoadAccessAsync()
    {
        if (SelectedShare is null) return;

        AccessErrorMessage = null;

        try
        {
            ShowAccessPanel = true;
            AllUsers = (await _userRepo.GetAllAsync()).ToList();
            AllGroups = (await _groupRepo.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the access rights");
            AccessErrorMessage = Resources.Web_Error_LoadAccessRightsFailed;
        }
    }

    public string GetPrincipalDisplayName(Guid principalId)
    {
        var user = AllUsers.FirstOrDefault(u => u.Id == principalId);
        if (user is not null) return user.Name ?? user.Username;

        var group = AllGroups.FirstOrDefault(g => g.Id == principalId);
        if (group is not null) return group.Name;

        return principalId.ToString()[..8] + "…";
    }

    public string GetPrincipalType(Guid principalId)
    {
        if (AllUsers.Any(u => u.Id == principalId)) return "user";
        if (AllGroups.Any(g => g.Id == principalId)) return "group";
        return "unknown";
    }
}
