using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Creates short-lived capability tokens for backup downloads after the Blazor
/// circuit authorized the user (mirrors <see cref="LogDownloadTokenService"/>).
/// The token carries only the backup file name; the controller re-validates it
/// against the backup folder before streaming.
/// </summary>
public sealed class BackupDownloadTokenService
{
    private readonly ITimeLimitedDataProtector _protector;

    public BackupDownloadTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector("Kaimo.FileServer.DatabaseBackup.Download.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(string fileName)
        // Links are rendered once with the backup list, so allow a comfortable
        // window between page load and the user clicking download.
        => _protector.Protect(fileName, TimeSpan.FromMinutes(15));

    public bool TryUnprotect(string token, out string fileName)
    {
        try
        {
            fileName = _protector.Unprotect(token, out _);
            return !string.IsNullOrWhiteSpace(fileName);
        }
        catch
        {
            fileName = string.Empty;
            return false;
        }
    }
}
