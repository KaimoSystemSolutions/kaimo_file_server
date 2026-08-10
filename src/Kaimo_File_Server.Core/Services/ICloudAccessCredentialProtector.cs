namespace Kaimo_File_Server.Core.Services;

public interface ICloudAccessCredentialProtector
{
    string Protect(IReadOnlyDictionary<string, string> credentials);
    Dictionary<string, string> Unprotect(string protectedCredentials);
}
