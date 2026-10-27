using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Components.Pages.Settings.Components;

public partial class VersioningSettingsTab
{
    private const long Mb = 1024 * 1024;

    [Inject] private IVersioningPolicyStore Store { get; set; } = default!;
    [Inject] private ILogger<VersioningSettingsTab> Logger { get; set; } = default!;

    /// <summary>Whether the viewer may change global settings; the page only renders this tab for them.</summary>
    [Parameter] public bool CanManage { get; set; }

    private VersioningPolicy _policy = VersioningPolicy.Default();
    private long _maxFileSizeMb;
    private string _patternsText = "";
    private bool _busy;
    private bool _success;
    private string? _error;

    private bool Locked => _busy || !CanManage;

    private static string T(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;

    protected override async Task OnInitializedAsync() => Apply(await Store.GetAsync());

    private void Apply(VersioningPolicy policy)
    {
        _policy = policy;
        _maxFileSizeMb = policy.MaxFileSizeBytes / Mb;
        _patternsText = string.Join('\n', policy.ExcludePatterns);
    }

    private void RestoreDefaultPatterns()
        => _patternsText = string.Join('\n', VersioningPolicy.DefaultExcludePatterns);

    private async Task SaveAsync()
    {
        // Defence in depth: the page gates the tab, but an event must never save without the right.
        if (!CanManage) return;

        _busy = true;
        _success = false;
        _error = null;
        try
        {
            var policy = _policy.Clone();
            policy.MaxFileSizeBytes = Math.Max(0, _maxFileSizeMb) * Mb;
            policy.ExcludePatterns = _patternsText.Split('\n').ToList();
            // Trims blank lines and clamps out-of-range numbers before they leave the page.
            policy.Normalize();
            await Store.SetAsync(policy);
            Apply(await Store.GetAsync());
            _success = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Saving the versioning policy failed");
            _error = T("Web_VersioningSettings_SaveFailed", "The versioning settings could not be saved.");
        }
        finally
        {
            _busy = false;
        }
    }
}
