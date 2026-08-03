namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Issues short-lived, single-use tickets that bridge an authorized Blazor
/// action to a provider's HTTP OAuth redirect flow.
/// </summary>
public interface ICloudAuthorizationTicketStore
{
    string Issue(Guid shareId, string localPath, string providerId);
    bool IsValid(string token, Guid shareId, string localPath, string providerId);
    bool TryConsume(string token, Guid shareId, string localPath, string providerId);
}
