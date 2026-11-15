namespace Kaimo_File_Server.Web.Services.Downloads;

/// <summary>
/// A one-time capability token for streaming a set of share items to the browser as a ZIP.
/// Issued by the (authenticated) file browser after resolving the signed-in user; the
/// download controller resolves it back to the user + paths and re-checks access through
/// <c>IFileService</c> per entry before streaming. Mirrors <see cref="FileDownloadTicket"/>.
/// </summary>
public sealed record ZipDownloadTicket(Guid ShareId, Guid UserId, string[] RelativePaths, string ArchiveName);

/// <summary>Short-lived, single-use ticket store for ZIP downloads (see <see cref="OneTimeTicketStore{TTicket}"/>).</summary>
public sealed class ZipDownloadTicketStore() : OneTimeTicketStore<ZipDownloadTicket>(TimeSpan.FromMinutes(5));
