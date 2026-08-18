using Microsoft.AspNetCore.Components.Forms;

namespace Kaimo_File_Server.Web.Services;
// Services/FileUploadCoordinator.cs
public class FileUploadCoordinator
{
    public event Func<InputFileChangeEventArgs, Task>? OnFilesSelected;
    public event Action? OnUploadRequested;
    public event Action? OnFilesProcessed;

    public void RequestUpload() => OnUploadRequested?.Invoke();

    public Task NotifyFilesSelected(InputFileChangeEventArgs e)
        => OnFilesSelected?.Invoke(e) ?? Task.CompletedTask;

    /// <summary>Signals that BrowserFile streams are no longer in use and the input can be recreated.</summary>
    public void NotifyFilesProcessed() => OnFilesProcessed?.Invoke();
}
