namespace Kaimo_File_Server.Web.Services.Downloads;

public sealed record CloudAccessDownloadTicket(Guid ShareId, Guid UserId, string RelativePath, string FileName);

/// <summary>Short-lived, single-use ticket store for virtual share downloads.</summary>
public sealed class CloudAccessDownloadTicketStore() : OneTimeTicketStore<CloudAccessDownloadTicket>(TimeSpan.FromMinutes(5));
