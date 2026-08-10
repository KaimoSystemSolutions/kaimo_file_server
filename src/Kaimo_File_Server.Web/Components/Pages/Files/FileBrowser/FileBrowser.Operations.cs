
using System.Collections;
using System.Diagnostics;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser.components;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.DynamicHelpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Components.Pages.Files.FileBrowser;

public partial class FileBrowser
{
// ========== Zip ==========

    public async Task ArchiveFiles(IEnumerable<FileMetadata> items, string format)
    {
        var itemList = items.ToList();
        var archiveName = itemList.Count == 1
            ? Path.GetFileNameWithoutExtension(itemList[0].Name) + format
            : Resources.Web_Archive_DefaultName + format;

        var toastId = Toast.Show(string.Format(Resources.Web_Archive_Progress, archiveName), ToastType.Progress);

        var result = await VM.ArchiveAsync(itemList, format);

        if (result.Success)
        {
            Toast.Update(toastId, string.Format(Resources.Web_Archive_Success, archiveName), type: ToastType.Success);
            await VM.LoadShareAsync(ShareName, VM.CurrentPath);
            StateHasChanged();
        }
        else
        {
            Toast.Update(toastId, result.Error ?? Resources.Web_Archive_Error, type: ToastType.Error);
        }
    }

    // ========== Unzip ==========

    public async Task UnzipFile(FileMetadata file)
    {
        var toastId = Toast.Show(string.Format(Resources.Web_Unzip_Progress, file.Name), ToastType.Progress);

        var result = await VM.UnzipAsync(file);

        if (result.Success)
        {
            Toast.Update(toastId, string.Format(Resources.Web_Unzip_Success, file.Name), type: ToastType.Success);
            await VM.LoadShareAsync(ShareName, VM.CurrentPath);
            StateHasChanged();
        }
        else
        {
            Toast.Update(toastId, result.Error ?? Resources.Web_Unzip_Error, type: ToastType.Error);
        }
    }
}

