using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Startup;

/// <summary>ASP.NET Core Data Protection key ring and its key-encryption certificate.</summary>
internal static class DataProtectionSetup
{
    /// <summary>Returns the configured certificate path, or null when the legacy co-located certificate is used.</summary>
    public static string? AddKaimoDataProtection(this WebApplicationBuilder builder, string applicationDataPath)
    {
        var dataProtection = builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(applicationDataPath, ".dp-keys")))
            .SetApplicationName("KaimoFiles");
        // Preferably kept apart from the key ring (DataProtection:CertificatePath, e.g. a Docker
        // secret); otherwise generated next to it in the application data (legacy default).
        var dataProtectionCertificatePath = builder.Configuration["DataProtection:CertificatePath"];
        var dataProtectionCertificate = string.IsNullOrWhiteSpace(dataProtectionCertificatePath)
            ? DataProtectionKeyEncryptionCertificate.LoadOrCreate(applicationDataPath)
            : DataProtectionKeyEncryptionCertificate.LoadFromFile(dataProtectionCertificatePath);
        dataProtection.ProtectKeysWithCertificate(dataProtectionCertificate);

        return dataProtectionCertificatePath;
    }
}
