namespace Kaimo_File_Server.Web.Services.Downloads;

/// <summary>
/// A one-time capability token for streaming a local share file to the browser.
/// The Blazor circuit (which already knows the signed-in user) issues a ticket;
/// the download controller resolves it back to the user + share-relative path and
/// re-checks access through <c>IFileService</c> before streaming.
/// </summary>
public sealed record FileDownloadTicket(Guid ShareId, Guid UserId, string RelativePath, string FileName);

/// <summary>Short-lived, single-use ticket store for file downloads (see <see cref="OneTimeTicketStore{TTicket}"/>).</summary>
public sealed class FileDownloadTicketStore() : OneTimeTicketStore<FileDownloadTicket>(TimeSpan.FromMinutes(5));
