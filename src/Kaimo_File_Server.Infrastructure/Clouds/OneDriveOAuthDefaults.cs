namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>Shared Microsoft identity endpoints and least-privilege runtime scopes.</summary>
public static class OneDriveOAuthDefaults
{
    public const string ClientId = "e966f5be-e8a1-4c67-b322-aac34c1ab642";
    public const string Authority = "common";
    public const string Scope = "offline_access Files.ReadWrite User.Read";
    public const string DeviceCodeEndpoint =
        "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";
    public const string TokenEndpoint =
        "https://login.microsoftonline.com/common/oauth2/v2.0/token";
}
