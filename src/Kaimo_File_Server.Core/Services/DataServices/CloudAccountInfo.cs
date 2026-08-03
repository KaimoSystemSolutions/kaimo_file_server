namespace Kaimo_File_Server.Core.Services.DataServices;

/// <summary>
/// Provider-neutral account information that may be shown in management UIs.
/// Providers may omit either value when their API does not expose it.
/// </summary>
public sealed record CloudAccountInfo(string? DisplayName, string? Email, string? PictureUrl);
