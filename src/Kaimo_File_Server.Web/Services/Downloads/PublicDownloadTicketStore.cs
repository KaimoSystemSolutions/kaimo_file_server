namespace Kaimo_File_Server.Web.Services.Downloads;

/// <summary>
/// A one-time capability token for an anonymous public-share download. Issued by the public
/// landing page only after the link's policy (window) and optional password have been
/// validated on the circuit, so nothing sensitive rides the browser GET. The public download
/// controller resolves it back to the link <see cref="Token"/> and the selected paths, then
/// atomically consumes one access and streams under the link creator's identity.
/// <see cref="VisitId"/> identifies the visitor's page session so "link accessed" is notified
/// once per visit; null notifies on every download.
/// </summary>
public sealed record PublicDownloadTicket(
    string Token, string[] RelativePaths, string DownloadName, bool Zip, Guid? VisitId = null);

/// <summary>Short-lived, single-use ticket store for public downloads (see <see cref="OneTimeTicketStore{TTicket}"/>).</summary>
public sealed class PublicDownloadTicketStore() : OneTimeTicketStore<PublicDownloadTicket>(TimeSpan.FromMinutes(5));
