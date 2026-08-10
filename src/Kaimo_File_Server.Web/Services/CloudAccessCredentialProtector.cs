using System.Text.Json;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Purpose-isolated authenticated encryption for persisted provider tokens.</summary>
public sealed class CloudAccessCredentialProtector(IDataProtectionProvider provider)
    : ICloudAccessCredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(
        "KaimoFiles.CloudAccess.Credentials", "v1");

    public string Protect(IReadOnlyDictionary<string, string> credentials)
        => "dp:v1:" + _protector.Protect(JsonSerializer.Serialize(credentials));

    public Dictionary<string, string> Unprotect(string protectedCredentials)
    {
        const string prefix = "dp:v1:";
        if (!protectedCredentials.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The Cloud Access credential format is unsupported.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
                   _protector.Unprotect(protectedCredentials[prefix.Length..]))
               ?? throw new InvalidOperationException("The Cloud Access credentials are invalid.");
    }
}
