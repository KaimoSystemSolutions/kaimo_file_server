using System.Text.Json;
using Kaimo_File_Server.Core.Logging;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Creates short-lived capability URLs after the Blazor circuit authorized the user.</summary>
public sealed class LogDownloadTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimeLimitedDataProtector _protector;

    public LogDownloadTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector("Kaimo.FileServer.SystemLogs.Download.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(LogArchiveQuery query)
    {
        var search = query.SearchText?.Trim();
        if (search is { Length: > 200 })
            search = search[..200];
        var safeQuery = new LogArchiveQuery(
            query.Sources.Take(20).ToArray(),
            query.MinimumLevel,
            search,
            1000,
            query.UtcDate);
        return _protector.Protect(JsonSerializer.Serialize(safeQuery, JsonOptions), TimeSpan.FromMinutes(2));
    }

    public bool TryUnprotect(string token, out LogArchiveQuery query)
    {
        try
        {
            var json = _protector.Unprotect(token, out _);
            query = JsonSerializer.Deserialize<LogArchiveQuery>(json, JsonOptions)!;
            return query is not null;
        }
        catch
        {
            query = null!;
            return false;
        }
    }
}
