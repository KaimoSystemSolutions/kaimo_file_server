using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Core.Services.ExternalStorage;

/// <summary>
/// The provider behind a virtual share cannot be reached right now (connection not ready,
/// authentication or transport failure, server error). Transports report a retryable 503.
/// </summary>
public sealed class RemoteShareUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// A folder can be seen, but the sync transport may not read it, so it must not be picked as
/// a sync folder. <see cref="Folders"/> still lets the picker navigate to a usable child.
/// </summary>
public sealed class RemoteFolderNotSyncableException(
    IReadOnlyList<RemoteStorageItem> folders,
    string code,
    Exception? innerException = null)
    : IOException("The remote folder can be listed but not synchronized.", innerException)
{
    public IReadOnlyList<RemoteStorageItem> Folders { get; } = folders;
    public string Code { get; } = code;
}

/// <summary>The authenticated remote system rejected an operation.</summary>
public sealed class RemoteStorageAccessDeniedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>The remote system definitively refused this item (missing, wrong type, out of space): retrying cannot help.</summary>
public sealed class RemoteItemRejectedException(string message) : IOException(message);

/// <summary>Exception that exposes only a sanitized provider code and HTTP status.</summary>
public sealed class ProviderRequestException : HttpRequestException
{
    public ProviderRequestException(string providerId, SanitizedProviderError error)
        : base(
            error.Detail is { Length: > 0 } detail
                ? $"{providerId} request failed with provider code '{error.Code}': {detail}"
                : $"{providerId} request failed with provider code '{error.Code}'.",
            null,
            error.StatusCode)
    {
        ProviderId = providerId;
        ErrorCode = error.Code;
        Category = error.Category;
    }

    public string ProviderId { get; }
    public string ErrorCode { get; }
    public ProviderErrorCategory Category { get; }
}
