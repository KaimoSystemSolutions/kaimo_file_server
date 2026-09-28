using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Domain.Notifications;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using MimeKit;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>A resolved mail recipient.</summary>
public sealed record MailRecipient(string Address, Guid? UserId, string Name);

/// <summary>
/// Resolves the <see cref="RecipientSpec"/>s of a rule to mail addresses. Disabled users and
/// users without a valid address are skipped, external addresses are validated, and the
/// result is de-duplicated by address.
/// </summary>
public sealed class NotificationRecipientResolver(
    IUserRepository users,
    IGroupRepository groups,
    IRoleRepository roles,
    IScopedRoleAssignmentRepository assignments)
{
    public async Task<List<MailRecipient>> ResolveAsync(
        IEnumerable<RecipientSpec> specs, IReadOnlyDictionary<string, Guid> contextUsers)
    {
        var result = new Dictionary<string, MailRecipient>(StringComparer.OrdinalIgnoreCase);

        void AddUser(User? user)
        {
            if (user is null || !user.IsEnabled || !TryNormalizeAddress(user.Email, out var address)) return;
            result.TryAdd(address, new MailRecipient(address, user.Id, user.Name));
        }

        foreach (var spec in specs)
        {
            switch (spec.Kind)
            {
                case RecipientKind.ContextRole:
                    if (contextUsers.TryGetValue(spec.Value, out var contextUserId))
                        AddUser(await users.GetByIdAsync(contextUserId));
                    break;

                case RecipientKind.User:
                    if (Guid.TryParse(spec.Value, out var userId))
                        AddUser(await users.GetByIdAsync(userId));
                    break;

                case RecipientKind.Group:
                    if (Guid.TryParse(spec.Value, out var groupId))
                        foreach (var member in await groups.GetMembersAsync(groupId))
                            AddUser(member);
                    break;

                case RecipientKind.PermissionHolder:
                    if (Enum.TryParse<ManagementPermission>(spec.Value, out var mask) && mask != ManagementPermission.None)
                    {
                        var holderIds = await ManagementAuthService.GetGlobalPermissionHolderUserIdsAsync(
                            assignments, roles, users, groups, role => (role.ManagementPermissions & mask) == mask);
                        foreach (var id in holderIds)
                            AddUser(await users.GetByIdAsync(id));
                    }
                    break;

                case RecipientKind.ExternalAddress:
                    if (TryNormalizeAddress(spec.Value, out var external))
                        result.TryAdd(external, new MailRecipient(external, null, external));
                    break;
            }
        }

        return result.Values.ToList();
    }

    /// <summary>Accepts a single plain mail address (no display name, no list).</summary>
    public static bool TryNormalizeAddress(string? value, out string address)
    {
        address = (value ?? string.Empty).Trim();
        return address.Length is > 2 and <= 320
               && !address.Contains(',') && !address.Contains(';') && !address.Contains('<')
               && MailboxAddress.TryParse(address, out var mailbox)
               && string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase)
               && address.Contains('@');
    }
}
