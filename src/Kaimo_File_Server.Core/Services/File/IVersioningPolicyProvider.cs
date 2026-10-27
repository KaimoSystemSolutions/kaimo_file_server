namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Supplies the current global <see cref="VersioningPolicy"/>. Implementations cache and
/// refresh it so a change made in one process (the Web settings page) reaches the others
/// (SMB bridge, Host) without a restart. Every call returns an instance the caller owns.
/// </summary>
public interface IVersioningPolicyProvider
{
    Task<VersioningPolicy> GetAsync();
}
