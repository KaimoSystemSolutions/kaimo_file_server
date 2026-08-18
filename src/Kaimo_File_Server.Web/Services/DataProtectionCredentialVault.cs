using System.Text.Json;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Protects credential payloads with ASP.NET Core Data Protection. Version 2
/// binds ciphertext to its connection and provider. Version 1 payloads remain
/// readable during the additive migration and are rewritten on the next save.
/// </summary>
public sealed class DataProtectionCredentialVault(IDataProtectionProvider provider) : ICredentialVault
{
    private const string CurrentPrefix = "dp:v2:";
    private const string LegacyPrefix = "dp:v1:";

    private readonly IDataProtector _rootProtector = provider.CreateProtector(
        "KaimoFiles.ExternalStorage.Credentials", "v2");

    private readonly IDataProtector _legacyProtector = provider.CreateProtector(
        "KaimoFiles.CloudAccess.Credentials", "v1");

    public string Protect<T>(T credential, CredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Validate(context);

        return CurrentPrefix + CreateContextProtector(context)
            .Protect(JsonSerializer.Serialize(credential));
    }

    public T Unprotect<T>(string protectedValue, CredentialContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        Validate(context);

        string json;
        if (protectedValue.StartsWith(CurrentPrefix, StringComparison.Ordinal))
        {
            json = CreateContextProtector(context)
                .Unprotect(protectedValue[CurrentPrefix.Length..]);
        }
        else if (protectedValue.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            // Legacy Cloud Access payloads were not context-bound. They are
            // accepted only as a transition path and become v2 on the next write.
            json = _legacyProtector.Unprotect(protectedValue[LegacyPrefix.Length..]);
        }
        else
        {
            throw new InvalidOperationException("The credential payload format is unsupported.");
        }

        return JsonSerializer.Deserialize<T>(json)
               ?? throw new InvalidOperationException("The credential payload is invalid.");
    }

    private IDataProtector CreateContextProtector(CredentialContext context)
        => _rootProtector.CreateProtector(
            context.ConnectionId.ToString("D"),
            context.ProviderId,
            context.CredentialKind,
            context.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Validate(CredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ConnectionId == Guid.Empty)
            throw new ArgumentException("A credential context requires a connection ID.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.ProviderId))
            throw new ArgumentException("A credential context requires a provider ID.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.CredentialKind))
            throw new ArgumentException("A credential context requires a credential kind.", nameof(context));
        if (context.FormatVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(context), "The credential format version must be positive.");
    }
}
