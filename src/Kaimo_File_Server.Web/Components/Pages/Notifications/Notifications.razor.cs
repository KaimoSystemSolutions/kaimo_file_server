using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.Notifications;
using Kaimo_File_Server.Infrastructure.Notifications;
using Kaimo_File_Server.Web.Components.Shared;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Notifications;

public partial class Notifications
{
    private enum NotifTab { Rules, Templates, Layout, Log }

    private NotifTab _tab = NotifTab.Rules;
    private bool _confirmDelete;
    private string? _logoError;
    private ElementReference _htmlEditor;

    // Rule tree: categories start collapsed so the tab opens as a short overview.
    private readonly HashSet<string> _openCategories = [];

    private bool AllCategoriesOpen => VM.CatalogByCategory.All(c => _openCategories.Contains(c.Key));

    private void ToggleCategory(string category)
    {
        if (!_openCategories.Remove(category)) _openCategories.Add(category);
    }

    private void ToggleAllCategories()
    {
        if (AllCategoriesOpen) _openCategories.Clear();
        else _openCategories.UnionWith(VM.CatalogByCategory.Select(c => c.Key));
    }

    private void OnRuleKey(KeyboardEventArgs e, Guid ruleId)
    {
        if (e.Key is "Enter" or " ") VM.SelectRule(ruleId);
    }

    private static string CategoryIcon(string category) => category switch
    {
        "users" => "users.svg",
        "security" => "shield.svg",
        "sharelinks" => "share.svg",
        "sync" => "sync-twoway.svg",
        "backup" => "archive.svg",
        "filebackup" => "archive.svg",
        "devices" => "refresh.svg",
        "certificates" => "globe.svg",
        _ => "mail.svg",
    };

    protected override async Task OnInitializedAsync()
    {
        await VM.LoadAsync();
        await VM.RefreshLayoutPreviewAsync();
    }

    private void SwitchTab(NotifTab tab)
    {
        _tab = tab;
        _confirmDelete = false;
        VM.ClearMessages();
    }

    private async Task DeleteRuleAsync()
    {
        _confirmDelete = false;
        await VM.DeleteRuleAsync();
    }

    private async Task InsertPlaceholderAsync(string name)
        => await JS.InvokeVoidAsync("mailTemplateEditor.insertAtCursor", _htmlEditor, VM.PlaceholderToken(name));

    private async Task OnLogoSelected(InputFileChangeEventArgs e)
    {
        _logoError = null;
        var file = e.File;
        if (file.Size > MailLayoutSettings.MaxLogoBytes)
        {
            _logoError = string.Format(R("Web_Notifications_LogoTooLarge"), MailLayoutSettings.MaxLogoBytes / 1024);
            return;
        }
        using var ms = new MemoryStream();
        await file.OpenReadStream(MailLayoutSettings.MaxLogoBytes).CopyToAsync(ms);
        _logoError = VM.SetLogo(ms.ToArray(), file.ContentType);
        await VM.RefreshLayoutPreviewAsync();
    }

    private async Task RemoveLogoAsync()
    {
        VM.ClearLogo();
        await VM.RefreshLayoutPreviewAsync();
    }

    private async Task RefreshLogAsync()
    {
        VM.ClearMessages();
        await VM.RefreshLogAsync();
    }

    // The stored body references the logo as an inline attachment; show the current logo instead.
    private string ViewHtml(MailDelivery delivery)
        => string.IsNullOrEmpty(VM.Layout.LogoDataUri)
            ? delivery.HtmlBody
            : delivery.HtmlBody.Replace("cid:" + MailTemplateRenderer.LogoContentId, VM.Layout.LogoDataUri);

    private static string EventName(string type)
        => NotificationEventCatalog.Find(type) is { } definition ? R(definition.NameKey) : type;

    private static StatusTone ToneOf(MailDeliveryStatus status) => status switch
    {
        MailDeliveryStatus.Sent => StatusTone.Positive,
        MailDeliveryStatus.Failed => StatusTone.Warning,
        MailDeliveryStatus.Dead => StatusTone.Negative,
        _ => StatusTone.Neutral,
    };

    private static string PermissionLabel(ManagementPermission permission)
        => NotificationsViewModel.SelectablePermissions.FirstOrDefault(p => p.Permission == permission).Label ?? permission.ToString();

    private static string R(string key) => NotificationsViewModel.R(key);
}
