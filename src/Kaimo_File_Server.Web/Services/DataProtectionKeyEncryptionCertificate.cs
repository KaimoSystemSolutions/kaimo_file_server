using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Creates and persists the internal certificate used to encrypt ASP.NET Data
/// Protection keys. The certificate is application-internal and is not served
/// as the HTTPS certificate.
/// </summary>
public static class DataProtectionKeyEncryptionCertificate
{
    private const string DirectoryName = ".dp-certificate";
    private const string FileName = "key-encryption.pfx";

    public static X509Certificate2 LoadOrCreate(string applicationDataPath)
    {
        var directory = Path.Combine(applicationDataPath, DirectoryName);
        var path = Path.Combine(directory, FileName);
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        if (!File.Exists(path))
            CreateAtomically(path);

        return Load(path);
    }

    /// <summary>
    /// Loads an existing, password-less PKCS#12 file supplied separately from the
    /// application data (e.g. a Docker secret), so a copy of the data volume or of its
    /// backups alone does not contain both the key ring and the key that decrypts it.
    /// Never creates a certificate: a missing file is a configuration error.
    /// </summary>
    public static X509Certificate2 LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "DataProtection:CertificatePath points to a file that does not exist. Provide the " +
                "key-encryption certificate there (e.g. as a Docker secret) or unset the setting.", path);
        return Load(path);
    }

    private static X509Certificate2 Load(string path)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password: null,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || certificate.GetRSAPrivateKey() is null)
        {
            certificate.Dispose();
            throw new CryptographicException(
                "The internal Data Protection certificate does not contain an RSA private key.");
        }

        return certificate;
    }

    private static void CreateAtomically(string destinationPath)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                "CN=Kaimo File Server Data Protection",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
                true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(30));
            var pfx = generated.Export(X509ContentType.Pfx);
            try
            {
                File.WriteAllBytes(temporaryPath, pfx);
                RestrictFile(temporaryPath);
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    // Another Web instance won the first-start race. Every
                    // instance loads the single certificate that reached the
                    // shared application-data path.
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
