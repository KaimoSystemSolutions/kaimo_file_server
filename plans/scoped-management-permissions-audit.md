# Plan: Scoped management permission checks — audit and fixes

Status: 2026-10-28 · Phases 1–4 implemented (not committed) · second pass done, Phase 5 open (§6) · Method: bottom-up, layer by layer
(permission model → authorization engine → assignment service → view models → peripheral features).

## 1. Summary

The authorization engine itself (`ManagementAuthService`) is consistent and the no-privilege-elevation gate
for role assignment (`CanAssignRoleAsync`, `CanAddGroupMembersAsync`) is sound. The serious problems sit one
layer above it: several write paths **move an entity into the actor's scope without asking whether the actor
controls the entity before the move**, and user management **never compares the target's privileges with
the actor's**. Combined, a department-scoped admin can become a global admin.

### 1.1 Escalation chains (verified by code reading)

All three work with the seeded `DepartmentAdmin` role (`EditDepartment`, `ResetPasswords`,
`ManageGroupMembers`, `ManageShareAcls` are all part of `ManagementPermission.DepartmentAdmin`).

| # | Chain | Result |
|---|---|---|
| C1 | Departments → edit own department → tick the global admin as member → save (`DepartmentViewModel.Edit.cs:135`) → Users → edit that admin → set a new password (`UserListViewModel.Users.cs:235`) | Takeover of a global admin account |
| C2 | Users → edit a group of the own department → add the global admin (`UserListViewModel.Groups.cs:168`) → the user is auto-added to the department (`:177-187`) → reset password as in C1 | Same as C1, without `EditDepartment` |
| C3 | Departments → edit own department → tick any foreign or Global share (`DepartmentViewModel.Edit.cs:175-191`) → share now belongs to the department → ACL editor grants self `FullControl` | Read/write access to every share in the system |

## 2. Findings by layer

Severity: **Critical** = privilege escalation beyond scope · **High** = scope violation with real impact ·
**Medium** = inconsistent/defense-in-depth gap · **Low** = hygiene, performance, semantics.

### Layer 1 — Permission model (`Core/Security/ManagementPermission.cs`, `ScopedRoleAssignment.cs`)

| ID | Sev. | Finding |
|---|---|---|
| L1-1 | Low | `AssignDepartments` (bit 18, "move users between departments") is never checked anywhere and not offered in the role editor. Department membership is gated by `EditDepartment` instead. Either wire it up (see F2) or mark it reserved. |
| L1-2 | Low | `EnableDisableUsers` is only checked globally on the service-access settings page; the per-user enable/disable toggle in the user editor uses `EditUserProfiles` (see L4-U4). |
| L1-3 | Low | System-wide bits (`ManageClientDevices`, `ViewSecurityMonitor`, `ManageSystemSettings`, …) can be put into roles and assigned at department/share scope; nothing defines what that means. Most pages evaluate them with `HasGlobalPermissionAsync`, two do not (L5-1). |

### Layer 2 — Authorization engine (`Core/Services/ManagementAuthService.cs`, repositories)

| ID | Sev. | Finding |
|---|---|---|
| L2-1 | **Critical** | `CanManageUserAsync` (`:46`) only checks that the target is a member of a department in scope. It never checks the **target's own authority**. A department admin may edit, disable, reset the password of, or delete a global admin or another department's admin as soon as that user is also a member of the department (users ↔ departments is M:N). This is the final step of C1/C2. |
| L2-2 | Medium | M:N membership: a user in departments A and B is fully manageable (incl. `DeleteUsers`) by the admin of A alone. Decide whether destructive actions need authority over **all** of the user's departments. |
| L2-3 | Low | ALL-bits checks are evaluated **per single role** (`HasPermission` in `AnyGrantAsync`, `HasGlobalPermissionAsync`), whereas `GetEffectivePermissionsAtAsync` takes the **union** over roles. An actor whose rights are split across two roles fails `HasGlobalPermissionAsync(FullAdmin)` / combined-bit checks. Either document "one role must carry all bits" or evaluate the union per scope. |
| L2-4 | Low | The actor's groups come from the `UserContext` snapshot; view models build it once per page load and cache it (`_actorContext`). Removing someone from a group that grants authority only takes effect after their page reloads. Assignments themselves are read fresh. |
| L2-5 | Low | N+1 queries per check: one role lookup per assignment, and `GetDescendantIdsAsync` loads the whole department table per department assignment; `GetAuthorizedShareIdsCoreAsync` issues one query per descendant. Fine today, grows with tree size and is executed many times per page load. |

### Layer 3 — Assignment service (`Core/Services/RoleAssignmentService.cs`)

| ID | Sev. | Finding |
|---|---|---|
| L3-1 | OK | `CreateAsync`/`DeleteAsync` correctly normalize "department Global" to Global scope and enforce the subset rule; `DeleteAsync` bounds revocation by the same authority; last-admin guard is present. |
| L3-2 | Medium | `CanAddGroupMembersAsync` (`:88`) only checks the roles the group grants. It does **not** check that the actor may manage the group's membership at all, and there is no counterpart for **removal** (comment: "Removing members needs no such check"). The user-side editor relies on it alone (L4-U2). |
| L3-3 | Medium | The principal of an assignment is unconstrained: a department admin with `AssignRoles` may grant a department-scoped role to *any* user or to the `Everyone` group. Authority stays bounded by the actor's own, but "every user of the system is admin of department A" is possible in one click. |

### Layer 4 — View models (enforcement points)

#### Departments (`Web/Components/ViewModels/Departments/`)

| ID | Sev. | Finding |
|---|---|---|
| L4-D1 | **Critical** | `SaveAsync` members diff (`DepartmentViewModel.Edit.cs:135-136`): any user of the system can be added to the department. Only `EditDepartment` on the department is checked, nothing about the user. → C1. |
| L4-D2 | **Critical** | `SaveAsync` shares diff (`:175-191`): any share (foreign departments, Global, even the home-folder share) can be pulled into the department. → C3. |
| L4-D3 | **High** | `SaveAsync` groups diff (`:156-172`): any group except `Everyone` can be pulled in, **including `Admins`** and groups of other departments. Pulling a foreign group that is used in ACLs and grants no roles lets the actor add members → file access outside scope. Pulling `Admins` makes its member list editable for removals. |
| L4-D4 | **High** | Parent change (`:120`): the new parent is not authorized and not validated server-side (exists, not self/descendant, not Global). An admin can move their department out from under the parent's admins or under a foreign department; a forged value can create a cycle. |
| L4-D5 | Medium | `CreateAsync` (`DepartmentViewModel.cs:266`) only checks `HasAnyPermissionAsync(EditDepartment)`; `CreateParentId` is not authorized (department admins can create top-level departments or children of foreign departments). |
| L4-D6 | Medium | Delete: the UI shows delete only for global admins (`:180`, `CanDelete = IsGlobalAdmin`), the server check (`:318`) accepts `EditDepartment` on the department. Department admins can delete their own department (and the assignments a global admin made there). Children keep a dangling `ParentDepartmentId` (no FK, `DepartmentRepository.DeleteAsync`), descendant assignments are not touched. |

#### Users and groups (`Web/Components/ViewModels/Users/`)

| ID | Sev. | Finding |
|---|---|---|
| L4-U1 | **High** | `SaveGroupAsync` (`UserListViewModel.Groups.cs:94`): the member picker lists **all** users (`:82`) and every newly selected user is auto-added to the group's department (`:177-187`). → C2. |
| L4-U2 | **High** | `SaveUserAsync` group set (`UserListViewModel.Users.cs:284-289`): `SetGroupsForUserAsync` overwrites the complete group list, gated only by `AssignGroups` on the user. The actor can **remove** the user from groups outside the actor's scope (Global groups, groups of other departments, `Admins` → `ReconcileAdminCouplingAsync(false)` revokes the global admin role, `:291-296`). Additions are checked for role grants only, not for group manageability (a Global ACL group can be joined). |
| L4-U3 | **High** | `CreateGroupAsync` (`:219-232`): only `HasAnyPermissionAsync(CreateGroups)`; `CreateGroupDepartmentId` (default Global) is not authorized. Groups can be created in foreign departments or Global. |
| L4-U4 | Medium | `SaveUserAsync` writes `IsEnabled`, `CanChangePassword` and service access (`UserListViewModel.Services.cs:41`) under `EditUserProfiles`; `EnableDisableUsers` is never checked here. |
| L4-U5 | Medium | `ConfirmDeleteRoleAsync` (`UserListViewModel.Delete.cs:73`): no server-side `IsSystemRole` guard (only `RoleDetailsPanel.razor:84`), no check that the role is a subset of the actor's permissions, no last-admin check for custom roles carrying `FullAdmin`, and assignments of the deleted role stay behind. `SaveRoleAsync` likewise lets a global `AssignRoles` holder **remove** bits they do not hold themselves (revocation not bounded like `RoleAssignmentService.DeleteAsync`). |

#### Shares (`Web/Components/ViewModels/Shares/`, `Acl/`)

| ID | Sev. | Finding |
|---|---|---|
| L4-S1 | **High** | `ChangeDepartmentAsync` (`ShareListViewModel.Settings.cs:172`): checks `EditShareSettings` on the share, but not on the **target** department (`:193`). A share-scoped manager can move a share to any department or to Global. |
| L4-S2 | Medium | `CreateShareCoreAsync` (`ShareListViewModel.Create.cs:39`) has no server-side permission check; it relies on the UI flag `CanCreateShare`. New shares always land in the Global department, so a department admin creates shares they cannot manage afterwards (only the owner ACL remains). |
| L4-S3 | Medium | Unreferenced folders (`ShareListViewModel.UnreferencedShares.cs:24, 70`): purging deletes data but is gated by `_manageAllShares`, i.e. **any** global `ShareAdmin` bit (e.g. only `ManageShareLinks`). Should require global `DeleteShares`. |
| L4-S4 | Low | `AclEditorViewModel.DeleteEntryAsync` (`:431`) deletes by id without checking that the entry belongs to the loaded `FileMetadataId` (Blazor makes forging hard; defense in depth). |
| L4-S5 | OK | ACL editor gate (`CanManageAclsAsync`) is scoped per share and excludes the home-folder share; share settings/rename/delete re-check per action. |

### Layer 5 — Peripheral features (spot checks)

| ID | Sev. | Finding |
|---|---|---|
| L5-1 | Medium | `ClientDeviceAdminViewModel` (`:97`, `:166`, `:209`) and `SecurityMonitorViewModel` (`:64`) use `HasAnyPermissionAsync`: a role holding `ManageClientDevices`/`ViewSecurityMonitor` at **any** scope sees and revokes devices of **all** users, incl. global admins, or sees the system-wide login log. Use `HasGlobalPermissionAsync` (or filter by users in scope). |
| L5-2 | Low | `FileBrowserViewModel.cs:345` sets `CanManageSyncs` via `HasAnyPermissionAsync(SyncAdmin)`; UI only, the mutations in `ExternalStorageSyncViewModel` re-check per share. Should use `CanManageShareAsync` for the current share to avoid showing dead controls. |
| L5-3 | OK | Cloud access, connections, backup download, restic download, mail server, notifications, log viewer: gates are global or per department/share as intended. |

## 3. Fix plan

Order follows the escalation chains: first close the final step (L2-1), then the "pull into scope" doors,
then the rest. Each step is test-first (CLAUDE.md TDD): write the failing tests, implement, run only those
tests, run the full suite once at the end.

### Phase 1 — Critical (closes C1–C3) — implemented 2026-10-28

Status: done, not committed. Tests: `Core/ManagementAuthServiceTests.TargetRank.cs` (unit) and
`Web/ScopedAdminEscalationDatabaseTests*.cs` (end-to-end with the real `ManagementAuthService`); full suite green.
Deviations from the text below, found while implementing:
- F2: the user-side check uses `AssignDepartments` (not `EditUserProfiles`): the actor must reach the user through
  an assignment holding `AssignDepartments`, plus the F1 rank check.
- F3: "control" over a group/share is `EditDepartment` on its **current** department (not `ManageGroupMembers` /
  `EditShareSettings`) — `DepartmentAdmin` has no `EditShareSettings`, so the original rule would have blocked
  moving a child department's share. Moved groups/shares are re-read from the store before the decision, so a stale
  or forged picker object can neither bypass the pinning nor overwrite other fields. Pinned items (`Everyone`,
  `Admins`, home-folder share) are never moved in, but may be released back to Global.
- Department editor: an unauthorized change rejects the whole save (`Web_Dept_ChangesNotPermitted`); the group editor
  rejects members the actor may not add to the group's department (`Web_Group_MembersNotPermitted`).
- `ManagementAuthService` gained an `IUserRepository` dependency (target's groups for the rank check).


**F1 — Target-privilege guard for user management (L2-1).**
- Add `ManagementAuthService.OutranksAsync(actor, targetUserId)` (private helper) — true when, for every
  effective assignment of the target (direct and via groups), the actor's effective permissions at that
  assignment's scope are a superset of the role's bits (the `CanAssignRoleAsync` subset rule without the
  `AssignRoles` requirement). A target without assignments is always outranked.
- Apply it inside `CanManageUserAsync` for every permission except the self-service path, so all callers
  (user editor, delete, `AclService.HomeAccess`) are covered without changing the interface.
- Behavior change to accept: a global `UserManager` can no longer reset the password of a global
  `Administrator`.
- Deleting a user (decision §4.2, L2-2): when `required` contains `DeleteUsers`, the actor must hold it for
  **every** department the user belongs to, not just one. A user without any explicit department (only the
  implicit Global membership) can only be deleted with global authority. Edit and password reset keep the
  "one department in scope + F1" rule.
- Tests (`ManagementAuthServiceTests`): department admin vs. member with Global role → false; vs. member
  with sibling-department admin role → false; vs. member with a subset role in the same department → true;
  global FullAdmin vs. global FullAdmin → true; target admin via group membership → false; delete of a user
  in departments A and B by the admin of A only → false, by an admin of A and B (or of a common parent) → true.

**F2 — Department membership requires authority over the user (L4-D1, L4-U1, L1-1).**
- Department membership becomes its own right (decision §4.1): adding or removing users to/from a department
  is gated by `AssignDepartments` instead of `EditDepartment`. `EditDepartment` keeps name, description,
  color, default permissions, parent, groups and shares.
- New `IManagementAuthService.CanAddUserToDepartmentAsync(actor, userId, departmentId)`:
  `CanManageDepartmentAsync(…, AssignDepartments)` **and** (user already in the department **or**
  `CanManageUserAsync(actor, userId, EditUserProfiles)` incl. F1). Removal: `AssignDepartments` on the
  department.
- Use it in `DepartmentViewModel.SaveAsync` members diff, in the department picker of `SaveUserAsync`
  (today `EditDepartment`, `UserListViewModel.Users.cs:264-271`, and `AuthorizedDepartmentsForAssign`,
  `UserListViewModel.cs:336`) and in `SaveGroupAsync` before the auto-add (adding a non-member to a
  department group without `AssignDepartments` there is refused with a clear message). Restrict the member
  pickers (`StartEditAsync`, `StartEditGroupAsync`) to users that pass, plus current members.
- Non-breaking: add `AssignDepartments` to the seeded `DepartmentAdmin` system role (`DatabaseSeeder.SystemData.cs`,
  updated on every startup) and to the `DepartmentAdmin` preset, so existing department admins keep the
  ability; `FullAdmin` already contains it. Custom roles can now leave it out. Offer the bit in the role
  editor (`UserListViewModel.Roles.cs` permission list) with label/description in **both** `.resx` files.
- Tests (`DepartmentViewModelDatabaseTests`, `UserListViewModelDatabaseTests`): department admin cannot add
  a foreign user / the global admin via department edit, user edit or group edit; a role with
  `EditDepartment` but without `AssignDepartments` cannot change members but can still edit the department;
  global admin can; existing members unaffected.

**F3 — Pulling groups and shares into a department requires control over them (L4-D2, L4-D3).**
- In `DepartmentViewModel.SaveAsync`: moving a group in requires `CanManageGroupAsync(actor, groupId,
  ManageGroupMembers)` at its current department; moving a share in requires `CanManageShareAsync(actor,
  shareId, EditShareSettings)`. Releasing to Global requires the same right (actor holds it already).
- Never move `PROTECTED_GROUPS` or the home-folder share (`IsUserHomes`); exclude them from the pickers.
- Tests: department admin cannot pull a Global share, a foreign share, `Admins`, a foreign group; global
  admin can move ordinary groups/shares.

### Phase 2 — High — implemented 2026-10-28

Status: done, not committed. Tests: `Web/ScopedAdminEscalationDatabaseTests.Scope.cs` (end-to-end, real
`ManagementAuthService`); full suite green. Notes from implementing:
- F4: parent pickers (`AvailableParents`, new `CreateParentOptions`) list only authorized parents; the picker can only be
  cleared to "top level" with global authority (`CanMoveToTopLevel` / `CanCreateTopLevel` → `AllowClear`). If the actor
  does not control the current parent, only the current parent is offered. New text `Web_Dept_InvalidParent`.
- F5: user-side group changes need `AssignGroups` on the **group's** department (in addition to the user); groups the
  actor cannot assign are not offered and stay untouched. Removing members (both sides) uses the new
  `RoleAssignmentService.CanRemoveGroupMembersAsync`, i.e. `CanAssignRoleAsync` for every role the group grants — so,
  exactly like adding, it needs `AssignRoles`. Behavior change: the existing test pinning "removing needs no check"
  (`UserListViewModelDatabaseTests.GroupJoinGuard`) was updated to the new rule. New text
  `Web_Error_GroupGrantsUnrevokableRoles`.
- F6: group department picker → `AuthorizedDepartmentsForGroupCreate` (`CreateGroups` scope), default = first entry.
- F7: share department picker → `ShareTargetDepartments` (`EditShareSettings` scope + current department).


**F4 — Department hierarchy changes (L4-D4, L4-D5).** Server-side validation of the new parent (exists,
not self/descendant, not Global). If the parent changes, require `EditDepartment` on the old and the new
parent (Global authority when either is top-level). `CreateAsync`: parent must be authorized; top-level only
for global `EditDepartment`.

**F5 — Group membership edits from the user side (L4-U2, L3-2).** Diff against the previous groups and touch
only groups the actor may manage (`CanManageGroupAsync(…, ManageGroupMembers)` or `AssignGroups` at the
group's department); keep all others untouched. Add `RoleAssignmentService.CanRemoveGroupMembersAsync`
mirroring `DeleteAsync` (revocation bounded by assign authority) and use it on both sides; leaving `Admins`
then requires global admin authority.

**F6 — Group creation scope (L4-U3).** `CanManageDepartmentAsync(actor, CreateGroupDepartmentId,
CreateGroups)`; default the picker to the first authorized department, Global only for global actors.

**F7 — Share department change (L4-S1).** Additionally require `CanManageDepartmentAsync(actor,
targetDepartmentId, EditShareSettings)` (Global target → global authority) and that the target exists.

### Phase 3 — Medium — implemented 2026-10-28

Status: done, not committed. Tests: `Web/ScopedAdminEscalationDatabaseTests.Medium.cs` (F8, F11, F13) and
`.MediumAreas.cs` (F9, F10, F12), end-to-end with the real `ManagementAuthService`; full suite green. Notes:
- F8: new per-selection flags `CanEditAccountEnabledForSelected` / `CanEditServiceAccessForSelected`
  (`EnableDisableUsers`) and `CanEditCanChangePasswordForSelected` (`ResetPasswords`); a locked toggle keeps the
  stored value. `EnableDisableUsers` is now offered in the role editor. Behavior change: custom roles with
  `EditUserProfiles` but without `EnableDisableUsers` lose the enable/services toggles (all seeded roles and the
  `UserAdmin`/`DepartmentAdmin`/`FullAdmin` presets carry the bit). New text `Web_User_FileServices_NoPermission`.
- F9: also the security nav entry (`MainLayout`) and the dashboard tile; `ManageClientDevices` added to the
  system-only filter of `ScopedRoleAssignmentForm`. The engine does not reject system bits at non-global scope
  (optional part skipped). `SearchDestinations` still uses `HasAnyPermissionAsync` (navigation hint only).
- F10: create form has a department picker (`CreateShareDepartments`, `CreateShares` scope; global actors default
  to Global); the server re-checks `CanManageDepartmentAsync(…, CreateShares)` and that the department exists.
  `CanCreateShare` now needs a department to create in. Purge → global `DeleteShares`, adopt → global `CreateShares`.
- F11: `RoleAssignmentService.DeleteRoleAsync` / `AuthorizeRoleChangeAsync` (new partial
  `RoleAssignmentService.Definitions.cs`). New text `Web_Error_SystemRoleUndeletable`.
- F12: children, groups and shares of the deleted department move to its parent (Global for a top-level
  department); see §7 R1.
- F13: `IManagementAuthService.IsPrincipalInScopeAsync`; pickers use `RoleAssignmentService.GetAssignableRecipientsAsync`.


- **F8** (L4-U4): changes to `IsEnabled` and service access require `EnableDisableUsers`; keep
  `CanChangePassword` under `ResetPasswords`.
- **F9** (L5-1, L1-3): `ClientDeviceAdminViewModel` and `SecurityMonitorViewModel` → `HasGlobalPermissionAsync`.
  Optionally reject assigning roles with system-only bits at non-global scope in `CanAssignRoleAsync`.
- **F10** (L4-S2, L4-S3): server-side create check in `CreateShareCoreAsync`; create shares in a chosen,
  authorized department instead of always Global; purge requires global `DeleteShares`.
- **F11** (L4-U5): server-side `IsSystemRole` guard for delete; deleting/demoting a role requires the removed
  bits to be within the actor's global permissions; last-admin guard for custom `FullAdmin` roles; delete the
  role's assignments with the role.
- **F12** (L4-D6, decision §4.3): deleting a department requires `EditDepartment` on its **parent**
  (`CanManageDepartmentAsync(actor, parentId, EditDepartment)`); a top-level department requires global
  `EditDepartment`. So an admin can delete departments below their own, never the department their
  assignment sits on. `CanDelete` in the UI becomes a per-selection check with the same rule instead of
  `IsGlobalAdmin`. Re-parent children to the deleted department's parent; remove assignments of the deleted
  department only (descendants keep theirs).
- **F13** (L3-3, decision §4.4): the recipient of an assignment must be reachable through the actor's **own**
  `AssignRoles` authority — Global, or a department assignment that covers the recipient:
  - user: member of a department (or descendant) covered by an actor assignment holding `AssignRoles`;
  - group: `Group.DepartmentId` covered by such an assignment. Groups of the Global department (incl.
    `Everyone`, `Admins`) are therefore reserved for global actors.
  - The recipient may sit outside the *target* scope as long as the actor reaches it, e.g. the admin of
    "Engineering" may grant a "Backend" role to a "Frontend" member.
  - Share-scoped `AssignRoles` reaches no principal (it covers no department), so such delegates can only
    remove assignments, not create them.
  - Implementation: new `IManagementAuthService.IsPrincipalInScopeAsync(actor, principalId, required)` built on
    the scope part of `CanManageUserAsync`/`CanManageGroupAsync` (without F1's rank check — granting rights to
    a higher-ranked user elevates nobody). Checked in `RoleAssignmentService.CreateAsync` before the subset
    gate; the principal pickers (`UserListViewModel.LoadAssignmentFormDataAsync`,
    `DepartmentViewModel.StartAddAssignmentAsync`) list only reachable principals. Removal is unchanged;
    existing out-of-scope assignments are not migrated.
  - Tests: department admin → user of a sibling department outside their reach → refused; → member of a
    descendant → allowed; → `Everyone` → refused; global admin → anyone; share-scoped delegate → refused.

### Phase 4 — Low / hygiene — implemented 2026-10-28

Status: done, not committed. Tests: `Core/ManagementAuthServiceTests.Evaluation.cs` (F14–F16),
`Web/ScopedAdminEscalationDatabaseTests.Freshness.cs` (F15 end-to-end),
`Web/AclEditorViewModelAuthorizationTests.EntryOwnership.cs` (F17), `Web/FileBrowserViewModelSyncPermissionTests.cs`
(F18); full suite green. Notes:
- F14: documented, not changed (class remarks of `IManagementAuthService`): checks are ALL-bits per role, the
  delegation gate and `GetEffectivePermissionsAtAsync` use the union. Changing the checks to the union would make
  "global admin" disagree with the last-admin guards, which count roles carrying `FullAdmin`. Pinned by a test.
- F15: solved in the engine instead of in every view model: `GetEffectiveAssignmentsAsync` reads the actor's group
  memberships from the store on each check (the `UserContext` snapshot is only a fallback when the repository returns
  nothing). Covers every caller, also reads.
- F16: no instance cache (the service is per circuit, so it would be stale for the whole session). Instead
  `IManagementAuthService.BeginEvaluationScope()` caches assignments, roles, descendants and shares per async flow
  while open (new partial `ManagementAuthService.Evaluation.cs`). Composite checks (`CanManageUserAsync`,
  `CanAddUserToDepartmentAsync`, `IsPrincipalInScopeAsync`, `CanAssignRoleAsync`, the scope lists) open one
  themselves; the picker loops of the department, user/group and assignment forms open one around their batch.
- F17: update and delete re-read the entries of the loaded file and refuse ids that are not among them
  (`Web_Error_EntryNotFound`). The existing "authorized delete" test now deletes an entry of the file.
- F18: `CanManageSyncs` = current share in `GetAuthorizedShareIdsAnyAsync(SyncAdmin)` — the share set of the sync
  overview (any sync bit, like before, but for this share). `CanManageShareAsync(SyncAdmin)` would have needed all
  four sync bits.


- **F14** (L2-3): document or change the per-role ALL-bits semantics.
- **F15** (L2-4): rebuild the actor context (or re-read group memberships) before mutations in long-lived view
  models.
- **F16** (L2-5): request-scoped cache for roles and the department tree inside `ManagementAuthService`.
- **F17** (L4-S4): verify ACL entry ownership before delete/update.
- **F18** (L5-2): `CanManageSyncs` via `CanManageShareAsync` for the current share.

## 4. Decisions

1. **Department membership right — decided 2026-10-28:** separate right `AssignDepartments` (see F2).
2. **M:N users (L2-2) — decided 2026-10-28:** deleting a user requires the permission in **all** of the
   user's departments (see F1).
3. **Department delete (L4-D6) — decided 2026-10-28:** whoever holds the management permission on the
   **parent** department (see F12).
4. **Assignment principals (L3-3) — decided 2026-10-28:** only users/groups the actor reaches through their own
   department (parent) or global authority (see F13).

## 5. Not covered by this audit

WebDAV/SMB/REST transports beyond `BrowseApiController` (file ACL path, not management), search result
filtering, `ShareLinkService` and `BackupCatalogService` in depth, Razor-level visibility. These consume the
same engine; after Phase 1 a second pass should check them against F1/F3. → Done 2026-10-28, see §6.

## 6. Second pass (2026-10-28) — peripheral areas against the new rules

Method: code reading of every remaining `IManagementAuthService` consumer outside Phases 1–4. No code changed.

### 6.1 Result per area

| Area | Result |
|---|---|
| WebDAV / SMB / REST (`BrowseApiController`, `SyncApiController`, `WebDavController`, `ShareGrpcService`) | **OK.** No management mutation on any transport; file access goes through `AclService`. The home-folder grant (`AclService.HomeAccess`) uses `CanManageUserAsync(…, AccessHomeContents)`, so F1 (rank) and F15 (fresh groups) apply to web, REST, WebDAV and SMB alike. Listing the `users` share root / SMB allow list uses "any scope" holders by design (the per-home decision stays per path). |
| Search (`SearchAclFilter`, `SearchApiController`, `GlobalSearch`) | **OK.** Every backend filters through `SearchAclFilter` → `HasAccessBatchAsync`, which includes the home grant. See P5-6 for a cosmetic picker issue. |
| Share links (`ShareLinkService`, `ShareLinkListViewModel`) | **Gap (F1 analogue).** Anonymous access runs as the **creator**, but managing a link only requires the link bit on the share — see P5-2, P5-3. Creation itself is fine (re-checked, upload probe under the actor's ACL; downloads re-check the creator's ACL and enabled state per request). F3/F7 moves: links keep serving under the creator's ACL; the new department's link managers see them — consistent. |
| Backup catalog (`BackupCatalogService*`) | **Gap.** Jobs are correct (all sources in scope, homes Global-only). Restore bypasses the home-folder rule and file ACLs — see P5-1, P5-4, P5-5. F3/F7 moves: backup points follow the share's **current** department; acceptable because F3 requires control before the move. |
| Razor visibility (`MainLayout`, `JobMenu`, `SearchDestinations`) | **Minor.** Some nav gates use `HasAnyPermissionAsync` while the page checks globally, or miss a bit the page accepts — see P5-7. No server-side gate depends on them. |

### 6.2 Findings

| ID | Sev. | Finding |
|---|---|---|
| P5-1 | **High** | **Home folders via backup bypass `AccessHomeContents` and F1.** `CanSeeRestoreShare` / `HasShareBitAsync` (`BackupCatalogService.Restore.cs:20-27`) admit the home-folder share for every **global** `RestoreFromBackup`/`RestoreBackupInPlace` holder. The seeded `BackupOperator` role has neither `AccessHomeContents` nor any user authority, yet can browse (`BrowseAsync`), download (`OpenDownloadAsync`) and restore into another share (`EnqueueRestoreAsync`, `OtherShare`) the home of **every** user, incl. global admins. Live access requires `AccessHomeContents` (a `GlobalDelegationOnly` bit) plus the F1 rank check per owner. Existing test `ScopedRestorer_SeesOnlyItsShares_NeverTheHomesShare` only covers the scoped case. |
| P5-2 | **High** | **Managing another user's link uses that user's file rights.** `ShareLinkListViewModel` lists every link on shares in scope with its revealed URL (`BuildRowsAsync` → `RevealToken`) and lets the actor clear the password, extend the window, raise `MaxAccessCount`/quotas, drop the extension filter and re-enable it (`ShareLinkList.razor:418-446`). Downloads/uploads then run as the **creator** (`PublicDownloadController`, `PublicShareFileBrowserViewModel`). A role with only `ManageShareLinks`/`ManageUploadLinks` on a share therefore reads (or writes) whatever a global admin's link exposes, even where the actor's own ACL denies it. |
| P5-3 | Medium | `ShareLinkListViewModel.SaveAsync` / `DeleteAsync` (`:96`, `:111`) and `ShareLinkService.SaveSettingsAsync` have **no server-side re-check**; they rely on the list loaded at page start (stale after revocation, cf. F15) and on the Settings tab gate. |
| P5-4 | Medium | **Restore rights bypass file ACLs.** Browse, download and "new folder" restore only need the restore bit on the share, never the actor's read ACL. Intended for an operator role, but undocumented. Additionally, a restore **without** `RestorePermissions` (the only option for `RestoreFromBackup`-only holders, `:197`) places copies under `folder/Restored-…`, where they inherit the parent's ACL: items that carried their own, narrower ACL become readable to everyone who reads the parent. `OtherShare` copies source content to readers of the target share. |
| P5-5 | Medium | `RestorePermissions` writes ACL entries (`BackupRestoreExecutor.ApplyAclAsync`) with `RestoreBackupInPlace` only; `ManageShareAcls` is never required. A backup operator can restore an older ACL state (e.g. one that still granted themselves access) without holding the ACL right. |
| P5-6 | Low | `GlobalSearch.LoadVisibleSharesAsync` (`:234-246`) offers every share in **any** `ShareAdmin` scope (e.g. only `ManageShareLinks`) in the share picker, including disabled/hidden ones, without a read ACL. Results stay ACL-filtered; only share names leak. |
| P5-7 | Low | Nav/page mismatches: Notifications nav `HasAnyPermissionAsync` vs. page global (`NotificationsViewModel.cs:585`); `_canManageSettings` (`MainLayout.razor:407`) is `HasAny`, so a non-global `ManageSystemSettings` holder sees and can cancel the system-wide reindex in `JobMenu` (`CancelReindex`, no server check); Backup nav and `SearchDestinations` omit the restore bits although `BackupAccess.CanAccessPage` accepts them (restore-only roles have no entry point). |

### 6.3 Proposed fixes (Phase 5, test-first as before)

- **F19** (P5-1): treat the home-folder share like the live grant. In `BackupCatalogService.Restore.cs`, for `IsUserHomes`: listing/browsing the share root needs `HasAnyPermissionAsync(AccessHomeContents)` in addition to the restore bit; every path below it (`BrowseAsync`, `OpenDownloadAsync`, `EnqueueRestoreAsync` items/folder) resolves the owner from the first segment and requires `CanManageUserAsync(actor, owner, AccessHomeContents)` (F1 included). Restoring a whole homes root in one go then requires authority over every owner touched. Tests: global `BackupOperator` → refused; global `Administrator` → allowed; holder of `AccessHomeContents` vs. a higher-ranked owner → refused.
- **F20** (P5-2, P5-3): editing, revealing the URL of, re-enabling or deleting a link created by someone else requires that the actor outranks the creator (reuse the F1 rule; expose it as `IManagementAuthService.CanActForUserAsync` or check `CanManageUserAsync(actor, creatorId, <link bit>)`) — or, simpler alternative to decide: only the creator and global link managers may edit; scoped managers may only **disable/delete** foreign links. Add a server-side re-check (`CanManageShareAsync` with the kind's bit + the creator rule) to `SaveAsync`/`DeleteAsync`; gate `SaveSettingsAsync` by global `ManageSystemSettings`.
- **F21** (P5-4, P5-5): document "restore bits imply read access to the share's backup content" in the permission descriptions (both `.resx`), require `ManageShareAcls` on the destination for `RestorePermissions`, and for restores without permissions either copy the restored folder's ACL from the manifest as **protected** entries or restrict the restored folder to the actor (owner-only) so restored data never becomes broader than before. Decision needed.
- **F22** (P5-6, P5-7): share picker → ACL read or `GetAuthorizedShareIdsAnyAsync` restricted to enabled/visible shares; nav gates aligned with the page gates (Notifications/Settings via global mask, Backup nav incl. restore bits); `CancelReindex` behind global `ManageSystemSettings`.

## 7. Review follow-up (2026-10-28)

Side effects of authorized changes that bypassed the guards of the operation they perform implicitly.
Tests: `Web/ScopedAdminEscalationDatabaseTests.SideEffects.cs`, `Infrastructure/PreserveScopedUserRightsMigrationTests.cs`.

| ID | Fix |
|---|---|
| R1 | Department delete: refused when an assignment at the department carries bits outside the actor's effective permissions there (`RoleAssignmentService.CoversAssignmentsAtAsync`, text `Web_Dept_AssignmentsBeyondOwnRights`); groups and shares move to the parent instead of Global. |
| R2 | Leaving a department drops the user's groups there: department editor requires `CanManageUserAsync(…, AssignDepartments)` (rank) and `CanLeaveDepartmentGroupsAsync` per removed member; the user editor checks the latter for every unchecked department before writing. The user editor no longer drops stored memberships of departments the user is not in. |
| R3 | `SaveGroupAsync` pulls only joining members into the group's department (the live warning likewise). |
| R4 | Migration `PreserveScopedUserRights`: custom roles with `EditUserProfiles` gain `EnableDisableUsers`, with `EditDepartment` gain `AssignDepartments`. System roles stay with the seeder. |
| R5 | Share department change also requires `EditShareSettings` on the share's current department (`CanChangeSelectedShareDepartment` disables the button). |
| R6 | New `IManagementAuthService.GetUserIdsInScopeAsync` (one query per covered department); the department member picker, the group member picker and `GetAssignableRecipientsAsync` filter in memory and run the full check (incl. rank) only for candidates. The rank check's target lookups are cached inside an evaluation scope (`ManagementAuthServiceTests.Batch.cs`). |
| R7 | `MainLayout` evaluates its nav checks in one evaluation scope; `AclService.HasAccessBatchAsync` opens one for the home-folder owners of a batch. |
| R8 | The group editor uses the department members it loaded for the picker; the save re-reads them once (never the stale set). |
| R9 | Parent pickers (`LoadCreateParentOptionsAsync`, `LoadAvailableParentsAsync`) use `GetAuthorizedDepartmentIdsAsync(EditDepartment)` once. |
| R10 | `SaveUserAsync` (~67 lines) delegates to `AuthorizeMembershipChangesAsync`, `ApplyDepartmentChangesAsync`, `ApplyGroupChangesAsync` (`UserListViewModel.UserGroups.cs`) and `ResolveSignInTogglesAsync`, `WouldRemoveLastAdminAsync`, `SaveProfileFieldsAsync`, `ResetPasswordAsync`, `ReloadSelectedUserAsync`. |
