namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// The share a batch operation started in, captured by
/// <see cref="IFileBrowserViewModel.CaptureOperationTarget"/>. The batch passes it with every
/// item, so the remaining items never follow the browser into another share the user opened
/// meanwhile. Each backend pins its own state in a derived record; callers treat it as opaque.
/// </summary>
public abstract record FileOperationTarget;
