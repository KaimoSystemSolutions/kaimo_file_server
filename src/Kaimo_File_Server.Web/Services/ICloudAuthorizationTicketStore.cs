namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Issues short-lived, single-use tickets that bridge an authorized Blazor
/// action to a provider's HTTP OAuth redirect flow.
/// </summary>
public interface ICloudAuthorizationTicketStore
{
    /// <summary>Issues a short-lived proof bound to one share, path, and provider.</summary>
    string Issue(Guid shareId, string localPath, string providerId);

    /// <summary>Checks a ticket without consuming it during multi-step authorization.</summary>
    bool IsValid(string token, Guid shareId, string localPath, string providerId);

    /// <summary>Atomically validates and consumes a ticket at the final OAuth hand-off.</summary>
    bool TryConsume(string token, Guid shareId, string localPath, string providerId);
}
