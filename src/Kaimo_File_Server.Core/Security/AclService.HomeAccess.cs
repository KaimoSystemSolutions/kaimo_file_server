using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Core.Security;

public partial class AclService
{
    // What an owner holds in their home (HomeDirectoryService): read/write everything, but the
    // home folder itself cannot be deleted or renamed. AdminAll is never granted.
    private const FilePermission HomeContent = FilePermission.ReadAll | FilePermission.WriteAll;

    /// <summary>
    /// Virtual allow of <see cref="ManagementPermission.AccessHomeContents"/> in the home-folder
    /// share: the share root may be listed (ReadAll, this folder only) and the homes
    /// <c>&lt;userId&gt;</c> of users in the actor's scope (Global, or a department containing the
    /// owner) are granted the owner's rights. Evaluated in the ACL service itself, so web, REST,
    /// WebDAV and SMB enforce it alike. Only consulted when the explicit entries did not already
    /// allow; explicit Deny entries still win. Decisions are cached per owner for one batch.
    /// </summary>
    private sealed class HomeAccessGrant(UserContext actor, IServiceProvider sp)
    {
        private readonly IManagementAuthService? _mgmt = sp.GetService<IManagementAuthService>();
        private readonly Dictionary<Guid, bool> _owners = new();
        private bool? _any;

        public async Task<FilePermission> ForAsync(string normalizedPath)
        {
            if (_mgmt is null)
                return FilePermission.None;

            if (normalizedPath.Length == 0)
            {
                _any ??= await _mgmt.HasAnyPermissionAsync(actor, ManagementPermission.AccessHomeContents);
                return _any.Value ? FilePermission.ReadAll : FilePermission.None;
            }

            var slash = normalizedPath.IndexOf('/');
            var home = slash < 0 ? normalizedPath : normalizedPath[..slash];
            // Homes are named HomeDirectoryService.HomePathOf(userId) = Guid "D" format.
            if (!Guid.TryParseExact(home, "D", out var owner))
                return FilePermission.None;

            if (!_owners.TryGetValue(owner, out var allowed))
                _owners[owner] = allowed = await _mgmt.CanManageUserAsync(
                    actor, owner, ManagementPermission.AccessHomeContents);

            if (!allowed)
                return FilePermission.None;
            return slash < 0 ? HomeContent & ~FilePermission.Delete : HomeContent;
        }
    }
}
