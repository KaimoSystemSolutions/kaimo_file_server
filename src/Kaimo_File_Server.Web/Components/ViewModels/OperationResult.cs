namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Simple result wrapper for UI operations.</summary>
public record OperationResult(bool Success, string? Error = null)
{
    /// <summary>
    /// The operation was refused because it would destroy content that cannot be kept as a
    /// version; repeating it with the user's explicit consent may succeed.
    /// </summary>
    public bool RequiresBackupConsent { get; init; }

    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}
