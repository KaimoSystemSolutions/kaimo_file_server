using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kaimo_File_Server.SmbBridge.Security;

/// <summary>
/// Loads the bridge server identity and validates client certificates against
/// the dedicated control-plane CA. The OS trust store is deliberately not used.
/// </summary>
public sealed class ControlPlaneTls : IDisposable
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _clientCa;

    private ControlPlaneTls(X509Certificate2 serverCertificate, X509Certificate2 clientCa)
    {
        ServerCertificate = serverCertificate;
        _clientCa = clientCa;
    }

    public X509Certificate2 ServerCertificate { get; }

    public static ControlPlaneTls Load(IConfiguration configuration)
    {
        string serverCertificatePath = RequireFile(
            configuration["ControlPlane:Tls:ServerCertificatePath"],
            "ControlPlane:Tls:ServerCertificatePath");
        string serverKeyPath = RequireFile(
            configuration["ControlPlane:Tls:ServerKeyPath"],
            "ControlPlane:Tls:ServerKeyPath");
        string clientCaPath = RequireFile(
            configuration["ControlPlane:Tls:ClientCaCertificatePath"],
            "ControlPlane:Tls:ClientCaCertificatePath");

        X509Certificate2 serverCertificate =
            X509Certificate2.CreateFromPemFile(serverCertificatePath, serverKeyPath);
        X509Certificate2 clientCa = X509CertificateLoader.LoadCertificateFromFile(clientCaPath);

        if (!serverCertificate.HasPrivateKey)
        {
            serverCertificate.Dispose();
            clientCa.Dispose();
            throw new InvalidOperationException(
                "The configured SMB bridge server certificate has no private key.");
        }

        return new ControlPlaneTls(serverCertificate, clientCa);
    }

    public bool ValidateClientCertificate(
        X509Certificate2? certificate,
        X509Chain? _,
        SslPolicyErrors __)
    {
        if (certificate is null)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_clientCa);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));

        if (!chain.Build(certificate) || chain.ChainElements.Count == 0)
            return false;

        X509Certificate2 root =
            chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
        return root.RawDataMemory.Span.SequenceEqual(_clientCa.RawDataMemory.Span);
    }

    public static string? GetClientId(X509Certificate2? certificate)
    {
        if (certificate is null)
            return null;

        string clientId = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return ControlPlaneAccessPolicy.IsKnownClient(clientId) ? clientId : null;
    }

    private static string RequireFile(string? configuredPath, string setting)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException($"{setting} must be configured.");

        string fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{setting} does not exist.", fullPath);

        return fullPath;
    }

    public void Dispose()
    {
        ServerCertificate.Dispose();
        _clientCa.Dispose();
    }
}
