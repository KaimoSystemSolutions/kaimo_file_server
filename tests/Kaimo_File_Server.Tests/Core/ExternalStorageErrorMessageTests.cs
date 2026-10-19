using System.Globalization;
using Kaimo_File_Server.Core.Language;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards that every error code <c>CloudSyncExecutionService.ClassifyError</c> can
/// persist has an explicit, localized message in BOTH resource files, so the sync
/// health banner never falls back to showing a raw code and the two languages stay
/// in sync.
/// </summary>
public sealed class ExternalStorageErrorMessageTests
{
    private static readonly string[] Codes =
    [
        "local_path_missing", "remote_path_missing", "connection_failed",
        "remote_access_denied", "sync_failed", "invalid_grant", "invalid_token",
        "interaction_required", "access_denied", "authorization_declined",
        "slow_down", "temporarily_unavailable"
    ];

    [Fact]
    public void EveryErrorCode_HasEnglishAndGermanMessage()
    {
        var english = CultureInfo.GetCultureInfo("en");
        var german = CultureInfo.GetCultureInfo("de");

        foreach (string code in Codes.Append("Unknown"))
        {
            string key = $"Web_ExternalStorage_ErrorCode_{code}";
            Assert.False(
                string.IsNullOrWhiteSpace(Resources.ResourceManager.GetString(key, english)),
                $"English message missing for {key}");
            Assert.False(
                string.IsNullOrWhiteSpace(Resources.ResourceManager.GetString(key, german)),
                $"German message missing for {key}");
        }

        Assert.False(string.IsNullOrWhiteSpace(
            Resources.ResourceManager.GetString("Web_ExternalStorage_SyncStarted", english)));
        Assert.False(string.IsNullOrWhiteSpace(
            Resources.ResourceManager.GetString("Web_ExternalStorage_SyncStarted", german)));
    }

    [Fact]
    public void KnownCodeResolvesToMessage_UnknownFallbackEmbedsTheRawCode()
    {
        var english = CultureInfo.GetCultureInfo("en");

        Assert.Equal(
            "The local folder no longer exists or cannot be accessed.",
            Resources.ResourceManager.GetString(
                "Web_ExternalStorage_ErrorCode_local_path_missing", english));

        string template = Resources.ResourceManager.GetString(
            "Web_ExternalStorage_ErrorCode_Unknown", english)!;
        Assert.Contains("http_418", string.Format(template, "http_418"));
    }
}
