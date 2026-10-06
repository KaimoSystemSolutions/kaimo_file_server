namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Issues short-lived, single-use tickets that bridge an authorized Blazor
/// action to a provider's HTTP OAuth redirect flow.
/// </summary>
public interface ICloudAuthorizationTicketStore
{
    /// <summary>Issues a short-lived proof bound to one share, path, and provider.</summary>
    Task<string> IssueAsync(
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a ticket without consuming it during multi-step authorization.</summary>
    Task<bool> IsValidAsync(
        string token,
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically validates and consumes a ticket at the final OAuth hand-off.</summary>
    Task<bool> TryConsumeAsync(
        string token,
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default);
}
