using Microsoft.AspNetCore.Components.Forms;

namespace Kaimo_File_Server.Web.Services;
// Services/FileUploadCoordinator.cs
public class FileUploadCoordinator
{
    // Batch id correlates a selection with its completion so each upload keeps its own
    // live <InputFile> element: a second upload started while the first still streams
    // must not recreate the shared input and invalidate the first batch's browser streams.
    public event Func<InputFileChangeEventArgs, Guid, Task>? OnFilesSelected;
    public event Action? OnUploadRequested;
    public event Action<Guid>? OnFilesProcessed;

    public void RequestUpload() => OnUploadRequested?.Invoke();

    public Task NotifyFilesSelected(InputFileChangeEventArgs e, Guid batchId)
        => OnFilesSelected?.Invoke(e, batchId) ?? Task.CompletedTask;

    /// <summary>Signals that batch <paramref name="batchId"/>'s BrowserFile streams are no
    /// longer in use and that input element can be released.</summary>
    public void NotifyFilesProcessed(Guid batchId) => OnFilesProcessed?.Invoke(batchId);
}
