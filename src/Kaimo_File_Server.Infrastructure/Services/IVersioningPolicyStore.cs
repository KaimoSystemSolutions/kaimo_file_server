using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>The readable and writable global <see cref="VersioningPolicy"/> (settings page).</summary>
public interface IVersioningPolicyStore : IVersioningPolicyProvider
{
    /// <summary>Normalizes and persists the policy; other processes pick it up within their refresh interval.</summary>
    Task SetAsync(VersioningPolicy policy);
}
