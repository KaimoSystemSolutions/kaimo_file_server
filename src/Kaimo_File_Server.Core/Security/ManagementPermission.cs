namespace Kaimo_File_Server.Core.Security
{
    /// <summary>
    /// Bitwise combinable management/administration permissions.
    /// These control WHO can manage users, groups, shares, and ACLs —
    /// separate from FilePermission which controls file-level access.
    ///
    /// Used in combination with ScopedRoleAssignment to determine
    /// what a delegated admin can do within their scope.
    /// </summary>
    [Flags]
    public enum ManagementPermission : long
    {
        None = 0,

        // -- User Management --
        CreateUsers = 1L << 0,
        DeleteUsers = 1L << 1,
        EditUserProfiles = 1L << 2,
        ResetPasswords = 1L << 3,
        EnableDisableUsers = 1L << 4,

        // -- Group Management --
        CreateGroups = 1L << 8,
        DeleteGroups = 1L << 9,
        ManageGroupMembers = 1L << 10,

        // -- Role & Permission Assignment --
        AssignGroups = 1L << 16,  // assign users to groups (within scope)
        AssignRoles = 1L << 17,  // assign roles (only ≤ own permissions)
        AssignDepartments = 1L << 18,  // move users between departments

        // -- Share Management --
        CreateShares = 1L << 24,
        DeleteShares = 1L << 25,
        EditShareSettings = 1L << 26,
        ManageShareAccess = 1L << 27,  // share-level access: enable/disable + visibility (hidden)
        ManageShareAcls = 1L << 28,  // manage file/folder ACLs within shares (incl. share root)

        // -- Department Management --
        EditDepartment = 1L << 32,
        ViewDepartment = 1L << 33,

        // -- System / Global Settings --
        ManageSystemSettings = 1L << 40,  // global settings, e.g. application language
        ManageDataServices = 1L << 41,  // start/stop data services (SMB, future NFS/FTP)
        ManageCertificates = 1L << 42,  // view/download/replace the HTTPS server certificate

        // -- Shortcuts --
        UserAdmin = CreateUsers | DeleteUsers | EditUserProfiles
                  | ResetPasswords | EnableDisableUsers,

        GroupAdmin = CreateGroups | DeleteGroups | ManageGroupMembers,

        ShareAdmin = CreateShares | DeleteShares | EditShareSettings
                   | ManageShareAccess | ManageShareAcls,

        DepartmentAdmin = UserAdmin | GroupAdmin | AssignGroups
                        | ManageShareAccess | ManageShareAcls
                        | EditDepartment | ViewDepartment,

        SystemAdmin = ManageSystemSettings | ManageDataServices | ManageCertificates,

        FullAdmin = UserAdmin | GroupAdmin | ShareAdmin
                  | AssignGroups | AssignRoles | AssignDepartments
                  | EditDepartment | ViewDepartment
                  | SystemAdmin
    }
}