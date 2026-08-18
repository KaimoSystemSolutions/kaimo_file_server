# External Storage Connections, Syncs, and Virtual Shares

## Architecture, Authentication, Credential Security, and Incremental Delivery Plan

Status: **proposed architecture and implementation roadmap**  
Audience: maintainers, security reviewers, operators, and contributors  
Last reviewed: **2026-08-18**

## 1. Purpose

Kaimo Files currently has two separate ways to use remote storage:

- **Cloud Sync** stores a provider and its authorization material inside a sync mapping on a local share.
- **Cloud Access** stores a reusable connection and exposes a selected remote folder as a virtual, web-only share.

This document defines a common architecture in which a connection is the single source of truth for provider identity, authorization material, health, and supported capabilities. Sync definitions and virtual shares reference that connection instead of owning independent credentials.

The design must support OneDrive and Google Drive first, followed by transports such as SMB, NFS, and rsync. It must remain easy to operate in Docker while providing enterprise-grade options for tenant-controlled application registrations, non-interactive service identities, external secret stores, auditability, and key rotation.

The work is intentionally split into independently deliverable phases. A contributor should be able to complete one phase without having to redesign the following phases.

## 2. Decisions

The following decisions are part of this proposal:

1. A connection is a durable first-class entity and survives the removal of an individual sync or virtual share.
2. A connection may be reused by multiple syncs and virtual shares when authorization permits it.
3. Credentials are never persisted in sync settings, share settings, logs, URLs, browser state, or plaintext configuration files.
4. Short-lived access tokens are held only in memory or an encrypted distributed cache. Refresh tokens and equivalent long-lived grants are encrypted at rest.
5. Provider application credentials and per-connection authorization grants are separate types of secrets with separate lifecycles.
6. Interactive delegated authorization and non-interactive enterprise authorization are separate connection modes.
7. The common abstraction describes capabilities. It does not pretend that OAuth, SMB passwords, SSH keys, NFS mounts, and service principals have identical semantics.
8. Virtual shares appear under **Shares**. Syncs and connections appear in a common external-storage administration area.
9. Deleting a connection is blocked while any sync or virtual share references it. Cascade deletion is not permitted.
10. Reauthorization updates a connection in place and preserves its ID and all consumers.
11. All UI text introduced during implementation must use the English and German resource files. Code comments, XML documentation, architecture documents, migration notes, and operational documentation must be written in clear English.
12. Repository commits remain the responsibility of the repository owner unless explicitly requested otherwise.
13. The default Microsoft setup is zero-configuration: every standard Kaimo installation uses the public Microsoft application ID shipped with Kaimo and authorizes through device code.
14. The shipped Microsoft application ID is a public identifier, not a client secret. Every installation remains independent and stores only its own encrypted authorization grants and tokens.
15. A tenant-owned Microsoft public application ID and tenant authority must be supported later as optional Docker environment overrides. This override is part of the target design but is not part of the current implementation task.
16. Advanced confidential-client, certificate, workload-identity, and application-only modes remain optional enterprise extensions. They must not make the default interactive setup more difficult.

## 3. Current implementation assessment

### 3.1 Useful foundations

Cloud Access already provides several useful building blocks:

- a durable connection ID;
- department ownership;
- connection state and account metadata;
- encrypted credential persistence through ASP.NET Core Data Protection;
- automatic creation of a persistent Data Protection key-encryption certificate;
- virtual shares referencing a connection;
- a provider-capability-based file-browser UI;
- single-use download and authorization tickets;
- OneDrive refresh-token rotation.

The relevant starting points are:

- [`StorageConnection`](../../src/Kaimo_File_Server.Core/Domain/StorageConnection.cs)
- [`CloudAccessShare`](../../src/Kaimo_File_Server.Core/Domain/CloudAccessShare.cs)
- [`DataProtectionCredentialVault`](../../src/Kaimo_File_Server.Web/Services/DataProtectionCredentialVault.cs)
- [`DataProtectionKeyEncryptionCertificate`](../../src/Kaimo_File_Server.Web/Services/DataProtectionKeyEncryptionCertificate.cs)
- [`BrowserCapabilities`](../../src/Kaimo_File_Server.Web/Components/ViewModels/BrowserCapabilities.cs)

### 3.2 Security and architecture gaps that must be addressed

The current Cloud Sync implementation stores provider data, including refresh tokens, in `SyncedFolder.Data`. That object is serialized into the `CloudSettings` JSON column of a local share. These credentials are not protected by the Cloud Access credential protector.

The current Google OAuth client secret is also present in the tracked Web `appsettings.json`. That credential must be treated as compromised: rotate or revoke it in Google Cloud, remove it from tracked configuration and built images, and review repository history and published image layers. Merely deleting it from the latest file is insufficient.

The current Google flow has useful state/ticket checks and requests offline access, but it also has the following gaps:

- the application secret is sourced from normal configuration with an image default;
- the implementation manually constructs OAuth requests instead of using a maintained provider library;
- PKCE is not used as defense in depth;
- the token endpoint response body may be returned to the caller on failure;
- the refresh token is persisted in plaintext Cloud Sync JSON;
- the broad `https://www.googleapis.com/auth/drive` scope is a restricted Google Drive scope and carries verification and, in some deployments, security-assessment obligations.

The current OneDrive device-code implementation correctly follows the provider polling interval, handles `authorization_pending` and `slow_down`, keeps the device code server-side, requests `offline_access`, and persists rotated refresh tokens for Cloud Access. However:

- it uses a fixed public client ID and the `common` tenant;
- device-code flow may be blocked by enterprise Conditional Access and is classified by Microsoft as a higher-risk authentication flow;
- transient device sessions are in process memory and are not multi-instance safe;
- Cloud Sync OneDrive refresh tokens still use the legacy plaintext JSON storage;
- raw provider response bodies must not be included in production exceptions or logs without structured redaction.

The existing implementations are therefore useful migration inputs, not the final enterprise authentication architecture.

## 4. Conceptual model

```text
StorageConnection
    |
    +-- RemoteLocation (optional reusable root)
            |
            +-- SyncDefinition ------ LocalShare + LocalPath
            |
            +-- RemoteShare --------- Share catalog + access grants

LocalShare -------------------------- Share catalog
```

### 4.1 Storage connection

`StorageConnection` replaces the cloud-specific connection concept.

It owns:

- stable ID;
- provider or transport ID;
- display name;
- owning department;
- authorization mode;
- reference to a deployment-level provider profile;
- encrypted per-connection credential payload;
- normalized non-secret settings;
- provider account, tenant, and subject identifiers;
- granted scopes or effective capability summary;
- state, last verification time, last successful use, and sanitized error code;
- optimistic concurrency version and timestamps.

It does not own:

- a local share or local path;
- a remote root selected for one particular use;
- a synchronization schedule;
- sync filters or bandwidth rules;
- virtual-share grants;
- live access-token objects.

Suggested states:

```text
PendingConfiguration
PendingAuthorization
Ready
Degraded
NeedsReauthorization
Disabled
```

`Degraded` represents a transient provider or network problem. `NeedsReauthorization` represents a terminal grant problem such as `invalid_grant`, revoked consent, a deleted service principal, or an unavailable secret profile.

### 4.2 Provider profile

A `ProviderProfile` represents deployment-level application identity and policy. It is not a user connection.

Examples:

- a built-in Microsoft public-client application ID;
- a customer-owned Microsoft Entra application registration and tenant ID;
- a customer-owned Google OAuth web client;
- a Google Workspace service-account or Workload Identity configuration;
- an SMB security policy specifying allowed authentication types;
- an rsync SSH host-key policy.

The database may store non-secret profile metadata, but private keys, client secrets, and vault credentials must be resolved through a secret reference.

Suggested fields:

```text
Id
ProviderId
Name
AuthorizationMode
TenantOrOrganizationId
PublicClientId
SecretReference
AllowedRedirectBaseUri
AllowedScopes
Enabled
```

### 4.3 Remote location

`RemoteLocation` is an optional reusable provider root:

```text
Id
ConnectionId
Name
RemotePath
ProviderItemId
ProviderDriveOrShareId
CreatedAtUtc
UpdatedAtUtc
```

It is useful when the same remote root is both synchronized and published as a virtual share. A first implementation may keep the remote root on each consumer and introduce this entity later, but every consumer must reference a `StorageConnection` from the first migration onward.

### 4.4 Sync definition

`SyncDefinition` replaces `ShareDefinition.CloudSettings.Folders` as the authoritative model:

```text
Id
ConnectionId or RemoteLocationId
LocalShareId
LocalPath
RemotePath
RemoteProviderItemId
Mode
Schedule
AdvancedSettings
Enabled
RunAsUserId
CreatedByUserId
CreatedAtUtc
UpdatedAtUtc
```

Runtime data such as last run, lease owner, progress, current job ID, and last sanitized error should live in a separate runtime record. A background run must not rewrite the whole configuration aggregate merely to update a timestamp or rotated credential.

### 4.5 Shares and share backends

A virtual share must be presented and managed under **Shares**, but it must not be forced into the current local `ShareDefinition` by inventing a filesystem path.

The incremental option is a unified `IShareCatalog` read model that combines local and remote share records. The long-term option is a common `Share` identity with one backend record:

```text
Share
    Id, Name, DepartmentId, Enabled, Kind, timestamps

LocalShareBackend
    ShareId, PhysicalPath, Hidden, RecycleEnabled

RemoteShareBackend
    ShareId, ConnectionId or RemoteLocationId, ReadOnly
```

Local ACLs and complete-share remote grants remain separate access models until a deliberate ACL unification project is completed.

## 5. Capability-based provider contracts

OneDrive, Google Drive, SMB, NFS, and rsync must share lifecycle contracts without sharing unsupported operations.

Suggested capabilities include:

```text
CanBrowse
CanRead
CanWrite
CanCreateDirectory
CanDelete
CanRename
CanMove
CanServerSideCopy
CanSync
CanResolveStableItemIds
CanWatchChanges
SupportsDelegatedAuthorization
SupportsApplicationAuthorization
RequiresHostMount
```

Suggested service boundaries:

```csharp
public interface IStorageConnectionProvider
{
    string Id { get; }
    ProviderCapabilities Capabilities { get; }
    IReadOnlyCollection<AuthorizationMode> AuthorizationModes { get; }

    Task<AuthorizationStartResult> BeginAuthorizationAsync(
        AuthorizationStartContext context,
        CancellationToken cancellationToken);

    Task<AuthorizationCompletionResult> CompleteAuthorizationAsync(
        AuthorizationCompletionContext context,
        CancellationToken cancellationToken);

    Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken);

    Task<ConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        StorageConnection connection,
        CancellationToken cancellationToken);
}
```

Browsing operations belong in an `IRemoteFileStore`. Generic comparison and transfer policy belong in a central sync engine. A native rsync adapter may implement an optimized sync transport without pretending that it can browse and mutate individual files through `IRemoteFileStore`.

## 6. Credential taxonomy

Treating every secret as a generic dictionary makes ownership and rotation unclear. Kaimo must distinguish the following classes.

| Credential class | Example | Owner | Persistence |
| --- | --- | --- | --- |
| Provider application identity | Google client secret, Entra application certificate | deployment/provider profile | Docker/Kubernetes secret, external vault, workload identity, or protected file reference |
| Connection authorization grant | user refresh token, consented tenant and account | storage connection | encrypted database payload |
| Workload identity configuration | tenant ID, client ID, service-account subject | provider profile or connection | non-secret metadata plus external key/federation reference |
| Short-lived access token | Graph or Google access token | live session | memory only; encrypted distributed cache only if required for multi-instance coordination |
| OAuth transaction material | state, nonce, PKCE verifier, device code | short-lived authorization transaction | server-side distributed store with TTL; never browser-readable except opaque state/session ID |
| Encryption root | Data Protection key encryption certificate, KMS key reference | deployment | persistent protected volume, Docker secret, HSM, KMS, or vault |

Client IDs, tenant IDs, account IDs, key IDs, and certificate thumbprints are identifiers rather than secrets. They must still be validated and must not be accepted from untrusted callbacks.

## 7. Credential protection

### 7.1 Required storage behavior

All long-lived connection authorization payloads must be encrypted and authenticated at rest. The record should contain at least:

```text
EncryptedCredentialPayload
CredentialFormatVersion
ProtectionKeyId or ProtectorPurposeVersion
CredentialUpdatedAtUtc
```

The plaintext payload is provider-specific but typed at the provider boundary. It may contain a refresh token and granted scopes, or a reference to a workload credential. It must not contain general sync or share configuration.

Access tokens must not be written to the database. A process may cache one until shortly before its expiry. The cache key must be the connection ID, not a mutable sync path.

### 7.2 Credential vault abstraction

Introduce a narrow `ICredentialVault` abstraction so storage connections do not depend directly on ASP.NET Core Data Protection:

```csharp
public interface ICredentialVault
{
    string Protect<T>(T credential, CredentialContext context);
    T Unprotect<T>(string protectedValue, CredentialContext context);
}
```

The context must bind ciphertext to at least the connection ID, provider ID, credential kind, and format version. Copying ciphertext to another connection must not produce valid credentials.

The default implementation may continue to use ASP.NET Core Data Protection. The interface allows later integration with Azure Key Vault, Google Cloud KMS, HashiCorp Vault, an HSM, or an enterprise secret broker without rewriting provider adapters.

### 7.3 Self-contained Docker mode

The default Docker installation should remain low effort:

1. On first startup, create the Data Protection key-encryption certificate atomically.
2. Store it and the key ring in the persistent application-data volume.
3. Apply `0700` to secret directories and `0600` to secret files on Unix.
4. Fail startup when the directory is not writable, permissions cannot be made safe, or multiple instances observe conflicting key material.
5. Back up the key ring and key-encryption material with the database.
6. Display a health warning if no successful backup acknowledgement has been recorded.

This protects against database-only disclosure and provides automatic operation. It does not create a separate trust boundary when an attacker obtains the complete application-data volume.

### 7.4 Hardened enterprise mode

Enterprise deployments should be able to select one of these roots of trust:

- external KMS or vault-backed protector;
- certificate or key mounted as a read-only Docker/Kubernetes secret;
- managed identity or workload identity where the host platform supports it;
- HSM/TPM-backed key provider.

The application must support key versioning and gradual rotation. Existing records remain decryptable with retired read-only keys while new writes use the active key. A background migration may rewrap records in bounded batches.

### 7.5 Rotation and concurrency

Refresh-token rotation must be serialized per connection across processes. The implementation must:

1. acquire a distributed connection credential lease;
2. load the latest encrypted credential and concurrency version;
3. refresh the access token;
4. atomically persist a rotated refresh token when returned;
5. release the lease only after persistence succeeds;
6. avoid acknowledging in-memory rotation before the database commit;
7. mark the connection `NeedsReauthorization` on terminal grant errors;
8. keep transient provider failures in `Degraded` without discarding valid credentials.

## 8. OAuth transaction security

Every interactive flow must use a server-side authorization transaction with a short lifetime, normally ten minutes or less.

The transaction must bind:

- random transaction ID with at least 256 bits of entropy;
- provider profile and authorization mode;
- connection ID;
- initiating Kaimo user ID;
- owning department;
- requested scopes;
- exact redirect URI;
- CSRF state;
- OIDC nonce when identity tokens are requested;
- PKCE verifier when the flow supports PKCE;
- creation and expiry timestamps.

The browser receives only an opaque, single-use state or session identifier. The callback must atomically consume the transaction before storing credentials. It must re-check the actor's connection-management permission and confirm that the connection still exists and is still pending authorization.

For multiple Web instances, authorization transactions and device-code sessions must use a shared store such as PostgreSQL or a distributed cache. Sticky sessions are not an enterprise security or reliability strategy.

Redirect URIs must be constructed from a configured external base URL, not untrusted forwarding headers. Only HTTPS callback URIs are allowed outside explicit local development mode. Proxy headers must be accepted only from configured trusted proxies.

Provider error bodies, authorization codes, device codes, access tokens, refresh tokens, assertions, and client secrets must be removed from user-visible responses and structured logs. Logs may contain a provider error code, HTTP status, correlation ID, connection ID, and sanitized reason category.

## 9. Microsoft OneDrive and SharePoint authorization

Kaimo should support three Microsoft modes.

### 9.1 Delegated authorization code with PKCE

This is an optional advanced enterprise mode for a server with a stable HTTPS URL. It is not the default setup path and must not be required for a normal self-hosted installation.

- Use a tenant-controlled Entra application registration.
- Use authorization code flow with PKCE and OIDC through MSAL or Microsoft.Identity.Web rather than hand-written token requests.
- Allow a tenant-specific authority. Do not force `common` for enterprise profiles.
- Prefer a certificate or workload identity over a client secret for confidential-client authentication.
- Load container certificates from a read-only secret path or external vault.
- Request only the delegated scopes required by the selected connection behavior.
- For the current signed-in user's OneDrive read/write use case, the baseline remains `Files.ReadWrite`, `User.Read`, and `offline_access`.
- Validate the returned tenant ID, account subject, issuer, and granted scopes against the provider profile before accepting the connection.
- Support tenant admin consent and surface a safe, copyable admin-consent link when user consent is disabled.

Reauthorization uses the same connection ID. If the account or tenant changes, the UI must show the change and require explicit confirmation before existing consumers resume.

### 9.2 Public-client device authorization

Device authorization is the default Microsoft authorization mode for Kaimo. It provides the lowest-effort self-hosted setup for personal accounts, normal installations, and servers without a usable callback URL.

- The Kaimo image ships with a public Microsoft application ID because a public client ID is an identifier, not a secret.
- All default installations may use this same application ID, while authorization grants, refresh tokens, account metadata, and connection state remain local to each installation.
- No central Kaimo service, shared token store, or shared installation database is involved in the default flow.
- The default authority is `common` so personal and organizational Microsoft accounts can use the standard setup.
- A future operator override must allow a tenant-owned public client ID and `common`, `organizations`, `consumers`, or a specific tenant ID/verified tenant domain.
- The override must be installation-local and supplied through Docker environment variables or equivalent configuration. It must not require source changes or a custom image.
- Polling must obey the provider interval and `slow_down` response.
- Device sessions must be stored server-side and expire automatically.
- The UI must clearly display the provider verification host to reduce phishing risk.
- No token may pass through browser JavaScript or query parameters.

Microsoft Conditional Access can block device-code flow and Microsoft identifies it as a higher-risk authentication flow. When a tenant blocks the default mode, Kaimo must show a clear, localized explanation and point the operator to the optional tenant-owned or confidential-client configuration. This limitation must not silently weaken security or cause Kaimo to fall back to another flow.

The target configuration shape is:

```text
ExternalStorage__Microsoft__ClientId=<tenant-owned public client ID>
ExternalStorage__Microsoft__Tenant=common|organizations|consumers|<tenant ID>
```

Both settings are optional. Omitting them uses the Kaimo public client ID and `common` authority. The exact names may be refined when implementation begins, but the zero-configuration fallback and environment-based override are architectural requirements.

### 9.3 Application identity without a user

For Microsoft 365 deployments that require unattended access, support a tenant-specific confidential client using client credentials.

- Prefer workload identity or a certificate over a client secret.
- Require administrator consent.
- Prefer Selected permissions such as `Sites.Selected` or the appropriate selected file/folder permission where the target resource supports them.
- Require the separate resource assignment step; consent to a Selected scope alone grants no resource access.
- Store only the selected site, drive, folder identifiers, tenant, and credential reference in Kaimo.
- Do not silently fall back to broad `Files.ReadWrite.All` or `Sites.ReadWrite.All` permissions.

Application authorization has different `/users`, `/sites`, and `/drives` addressing semantics from delegated `/me` access. The provider adapter must model this explicitly. It is not a drop-in token replacement for personal OneDrive.

## 10. Google Drive authorization

Kaimo should support two primary Google modes and one optional platform mode.

### 10.1 Delegated web-server authorization

This is the default interactive mode.

- Use Google's web-server authorization-code flow through the maintained Google client library.
- Use a cryptographically random state value and a server-side authorization transaction.
- Use PKCE with `S256` as defense in depth where supported by the selected client-library flow.
- Request `access_type=offline` when background sync is required.
- Do not force `prompt=consent` for every reauthorization. Request it only when a refresh token is required and no valid grant exists, because repeated forced consent can invalidate or proliferate grants.
- Persist the refresh token only in the encrypted connection credential payload.
- Keep access tokens in memory and let the library refresh them.
- Validate the account subject, hosted domain where configured, granted scopes, and email/account metadata before marking the connection ready.

Google web clients require an application client secret. A self-hosted public image cannot securely contain a universal Google web client secret. Therefore one of the following deployment choices is unavoidable:

1. the operator supplies a customer-owned Google OAuth client through a Docker/Kubernetes secret;
2. Kaimo operates a separately secured vendor OAuth broker and a verified production OAuth application;
3. a Google Workspace administrator configures a non-interactive service identity.

This document recommends option 1 for self-contained installations and option 3 for managed Workspace deployments. A vendor broker adds an external availability and trust dependency and must be a separate architectural decision.

The callback URL must exactly match a Google-authorized HTTPS redirect URI. The connection wizard should display the exact URI that the operator must register.

### 10.2 Scope selection and Google verification

The current full Drive scope permits the browse-and-sync behavior but is a restricted scope. Google documents `drive.file` as the preferred non-sensitive alternative, but it only grants access to files created by or explicitly opened/shared with the application, commonly through Google Picker.

Kaimo must offer explicit capability profiles:

| Profile | Suggested scope | Consequence |
| --- | --- | --- |
| Selected items | `drive.file` | Lower scope; only explicitly selected/shared items are available |
| Read-only full Drive | `drive.readonly` | Full browsing and download; restricted scope |
| Read/write full Drive | `drive` | Full sync and virtual-share mutations; restricted scope |

The UI must explain the consequence before authorization. It must not request full Drive access for a read-only virtual share if a narrower viable design has been selected.

Public production use of restricted Drive scopes requires Google verification. Google also documents a security-assessment requirement when restricted-scope data is stored on or transmitted through servers. Internal Workspace applications and some limited-use cases may have different verification treatment, but operators remain responsible for their Google organization and policy configuration.

### 10.3 Google Workspace service identity

For unattended enterprise access, support a service account authorized by a Workspace administrator.

Possible variants:

- a service account that has been granted access directly to a Shared Drive or selected resources;
- a service account with Domain-Wide Delegation that impersonates one configured Workspace subject;
- Workload Identity Federation instead of a downloaded service-account key when the deployment platform supports it.

Domain-Wide Delegation is highly privileged and requires a Workspace super administrator. Kaimo must:

- require an explicit impersonated subject;
- constrain operations to that subject's permissions and the granted scopes;
- show a permanent warning when domain-wide delegation is in use;
- audit the Kaimo actor responsible for each administrative operation;
- never accept arbitrary per-request impersonation subjects from a browser;
- prefer workload identity over exported JSON keys;
- accept a JSON key only through a read-only secret file as a compatibility fallback.

The service-account private key must not be copied into the connection database. The connection stores a secret reference and non-secret subject information.

## 11. Non-OAuth connection credentials

The same credential rules apply to future providers.

### SMB

- Prefer Kerberos or managed machine identity where available.
- Store password credentials only as encrypted connection grants.
- Record and verify server identity and allowed dialects.
- Do not permit silent downgrade to SMB1 or unsigned transport.
- Treat server certificate or SPN validation failures as connection failures, not warnings.

### rsync over SSH

- Prefer an installation-specific SSH key referenced through a Docker secret, agent, TPM, or vault.
- Persist the expected host key or CA trust policy and fail closed on changes.
- Never enable `StrictHostKeyChecking=no` as an automatic setup shortcut.
- Model rsync as sync-capable and not necessarily browser-capable.

### NFS

- Prefer an operator-managed mount or isolated helper with explicit export and host allowlists.
- For Kerberos NFS, keep keytabs in a read-only secret and use a dedicated principal.
- Do not grant the main Web container broad mount privileges merely to make setup automatic.
- Mark the provider `RequiresHostMount` when direct remote browsing is unavailable.

## 12. Docker and operator experience

The product should automate everything that does not require an external identity-provider administrator.

### 12.1 Automatic behavior

Kaimo should automatically:

- create and permission its local credential-protection material;
- initialize database schema and provider metadata;
- generate OAuth state, nonce, and PKCE material;
- construct callback and admin-consent URLs from validated configuration;
- refresh and atomically rotate tokens;
- test a connection after authorization;
- discover account metadata and available drives where permitted;
- transition health state and provide actionable reauthorization notices;
- clean expired authorization transactions;
- redact secrets from logs and diagnostics;
- expose non-secret readiness checks;
- verify that key material and persistent volumes survive a restart.

For the default Microsoft provider, Kaimo must also automatically select the shipped public client ID and `common` authority when no operator override is present. The connection wizard should therefore require only a connection name, department, and completion of Microsoft's device-code sign-in.

### 12.2 Unavoidable one-time external actions

Kaimo cannot safely automate the following without already holding an external administrator credential:

- creating or approving an Entra application in a customer's tenant;
- granting Microsoft application permissions to selected resources;
- creating a Google OAuth client and registering its redirect URI;
- completing Google OAuth verification for a public application;
- granting Google Workspace Domain-Wide Delegation;
- creating SMB/Kerberos identities or NFS exports.

The setup wizard and documentation should reduce these actions to exact, provider-specific checklists and then validate the result. The application must never bypass them by shipping a confidential credential inside a public image.

### 12.3 Secret injection

Preferred configuration order:

1. workload identity or external vault;
2. Docker/Kubernetes secret file;
3. protected host file mounted read-only;
4. environment variable for development or transitional compatibility only.

Support `*_FILE`-style settings or explicit secret references, for example:

```yaml
services:
  web:
    secrets:
      - google_oauth_client_secret
      - entra_client_certificate
    environment:
      ExternalStorage__Profiles__GoogleEnterprise__ClientSecretFile: /run/secrets/google_oauth_client_secret
      ExternalStorage__Profiles__MicrosoftEnterprise__CertificateFile: /run/secrets/entra_client_certificate

secrets:
  google_oauth_client_secret:
    file: ./secrets/google-oauth-client-secret
  entra_client_certificate:
    file: ./secrets/entra-client.pfx
```

No production client secret or private key may have an image default. Missing required secrets must fail the affected provider profile closed while allowing unrelated storage providers to run.

## 13. Authorization inside Kaimo

External provider consent does not replace Kaimo authorization.

Recommended management permissions:

```text
CreateConnections
EditConnections
DeleteConnections
UseConnections
AuthorizeConnections
ViewConnectionHealth
```

A sync creation requires both:

- `CreateSyncs` on the target local share; and
- `UseConnections` for the selected connection.

A virtual-share creation requires both:

- the relevant remote-share creation permission; and
- `UseConnections` for the selected connection.

Connection management is department-scoped. The initial safe rule should require the consumer and connection to belong to the same department. Cross-department reuse should require an explicit connection grant rather than implicit ancestor access.

Users who may use a connection must not automatically be able to reveal or export its credentials. Kaimo never displays stored refresh tokens, private keys, client secrets, password material, or recoverable credential payloads.

## 14. Connection lifecycle

### Create

1. Create a pending connection with provider profile, department, and authorization mode.
2. Re-check Kaimo permissions.
3. Complete interactive authorization or validate the workload credential reference.
4. Resolve provider account and tenant metadata.
5. Validate scopes and tenant policy.
6. encrypt and persist the grant;
7. test the connection and mark it ready.

### Use

1. Resolve the connection by ID.
2. Verify Kaimo authorization and connection state.
3. Open a provider session.
4. obtain or refresh a short-lived token under a per-connection lease;
5. execute only a capability allowed by the connection and consumer;
6. persist rotated credentials atomically;
7. record sanitized health and audit information.

### Reauthorize

Reauthorization updates the existing connection. Existing syncs and virtual shares remain attached but pause while the connection is not ready. A change of provider account, tenant, or authorization mode requires explicit confirmation and a compatibility check for every referenced remote root.

### Disable

Disabling a connection prevents new operations but retains configuration and references. Active operations should receive cooperative cancellation followed by a bounded shutdown period.

### Delete

Deletion is allowed only when usage count is zero. Revocation is attempted before local deletion when the authorization mode supports it. If provider revocation fails, the administrator may choose a clearly labeled local-only removal after acknowledging that the external grant can remain active. The event must be audited.

Database foreign keys from syncs and remote shares must use `Restrict`, never `Cascade`.

## 15. Administration UI

The navigation should evolve toward one **External Storage** or **Storage Connections** entry. Keeping the existing **Cloud Access** label is acceptable during migration, but it becomes misleading when SMB, NFS, and rsync are added.

The page should use large entity tabs similar to Users and Groups:

- **Syncs**
- **Connections**

The Shares page should use:

- **Local Shares**
- **Virtual Shares**

The connection detail pane should show:

- provider and authorization mode;
- account and tenant identity;
- effective scopes and capabilities;
- state and last verified time;
- profile and secret-source health without revealing secrets;
- referenced syncs and virtual shares;
- Test, Authorize/Reauthorize, Disable, and Delete actions;
- an audit-summary link.

Creation actions should be contextual:

- **Create sync from connection** starts with the selected connection and then asks for remote and local roots.
- **Create virtual share from connection** starts with the selected connection and opens the Shares workflow.

## 16. Audit and observability

Record security-relevant events with immutable IDs:

- connection created, authorized, reauthorized, disabled, or deleted;
- provider account or tenant changed;
- scopes changed;
- secret profile changed;
- credential rotated or rewrapped, without recording its value;
- authorization failed or grant revoked;
- connection attached to or detached from a sync/share;
- local-only deletion after failed provider revocation;
- use of an application identity or domain-wide delegation.

Logs and metrics may identify provider ID, connection ID, consumer ID, operation type, duration, result category, throttling, and correlation ID. Remote paths should be omitted or separately classified as potentially sensitive.

Recommended metrics:

```text
connection_auth_success_total
connection_auth_failure_total
connection_reauthorization_required_total
credential_refresh_success_total
credential_refresh_failure_total
credential_rotation_total
provider_request_duration_seconds
provider_throttle_total
active_storage_sessions
connection_health_state
```

## 17. Migration strategy

Migration must be additive, reversible until the final cleanup, and safe when the application restarts between phases.

### Legacy sync migration rule

Create one new connection for every legacy sync mapping initially. Do not automatically deduplicate credentials, even when provider and account metadata appear equal. Two grants may have different scopes, tenant policy, revocation state, or intended ownership. Administrators can merge compatible connections later through an explicit workflow.

Migration procedure:

1. read the legacy provider payload;
2. create a pending migration connection in the same department as the local share;
3. protect the credential payload through `ICredentialVault`;
4. create a `SyncDefinition` referencing the new connection;
5. validate that the new connection can access the configured remote root;
6. mark the migration complete with a source checksum;
7. retain the legacy mapping as read-only fallback until rollout verification succeeds;
8. remove plaintext legacy credentials in a later irreversible cleanup migration.

The migration must never write tokens to migration logs or exception details.

## 18. Incremental delivery plan

### Phase 0: immediate credential containment

- [ ] Revoke or rotate the Google client secret currently present in tracked configuration.
- [ ] Remove all OAuth client secrets from `appsettings*.json`, Compose defaults, examples, and image layers.
- [ ] Review repository history and published artifacts for the exposed credential.
- [ ] Add automated secret scanning to CI.
- [ ] Document the current plaintext Cloud Sync token exposure as a release blocker.
- [ ] Add regression tests proving that serialized shares and API responses do not expose tokens.

Exit criterion: no valid confidential provider credential is present in source, image defaults, examples, logs, or test snapshots.

### Phase 1: neutral connection domain

- [ ] Introduce `StorageConnection`, `ProviderProfile`, states, and authorization modes.
- [ ] Add connection repository and restrictive foreign keys.
- [ ] Rename cloud-specific interfaces where necessary without changing runtime behavior.
- [ ] Introduce `ManageConnections`/`UseConnections` authorization checks.
- [ ] Add usage-count and delete-restriction tests.

Exit criterion: existing Cloud Access OneDrive records can be represented by the neutral model without credential migration loss.

### Phase 2: credential vault and authorization transactions

- [x] Introduce `ICredentialVault` with versioned, context-bound protection.
- [ ] Add self-contained and external-secret resolution modes.
- [x] Move OAuth transactions to a shared TTL-backed store.
- [x] Add per-connection distributed refresh leases and optimistic concurrency.
- [x] Add centralized secret redaction and provider-error sanitization.
- [x] Add backup/restore and credential-envelope rewrap tests.
- [ ] Add encryption-root key-rotation tests for the selected external protector.

Exit criterion: two Web instances can authorize and refresh connections safely while sharing the same database and key configuration.

### Phase 3: Microsoft provider consolidation

- [ ] Adapt current Cloud Access OneDrive to `IStorageConnectionProvider`.
- [ ] Move current Cloud Sync OneDrive connections to the same provider path.
- [ ] Preserve device-code flow as the zero-configuration default using the shipped public Microsoft application ID.
- [ ] Add optional Docker environment overrides for a tenant-owned public client ID and authority without requiring a custom image.
- [ ] Add tenant-controlled authorization-code/PKCE mode using MSAL or Microsoft.Identity.Web.
- [ ] Add tenant, issuer, account, and scope validation.
- [ ] Add certificate/workload identity support for confidential profiles.
- [ ] Add application authorization with Selected permissions as a separate mode.

Exit criterion: a single Microsoft connection can support multiple syncs and virtual shares, refresh-token rotation is atomic, and enterprise tenants can avoid device-code flow.

### Phase 4: Google provider consolidation

- [ ] Remove the manual controller token exchange in favor of the maintained Google client library.
- [ ] Add customer-owned web OAuth profiles and secret-file loading.
- [ ] Add state, optional PKCE, callback validation, and safe provider errors.
- [ ] Implement explicit selected-item, read-only, and read/write scope profiles.
- [ ] Store all Google refresh tokens only in encrypted connection records.
- [ ] Add Workspace service identity and an explicit impersonated-subject model.
- [ ] Prefer Workload Identity Federation; support key-file fallback with warnings.

Exit criterion: no Google application secret or refresh token is present in normal configuration or share JSON, and the selected authorization mode is visible and auditable.

### Phase 5: first-class sync definitions

- [ ] Add `SyncDefinition` and runtime-state persistence.
- [ ] Move schedules, paths, filters, and bandwidth settings out of Share JSON.
- [ ] Change the sync engine to resolve a connection by ID.
- [ ] Separate generic sync policy from remote file-store operations.
- [ ] Implement the legacy one-connection-per-sync migration.
- [ ] Run old and new readers in verification mode before cutover.

Exit criterion: sync execution no longer reads credentials from `SyncedFolder.Data` and the legacy JSON contains no secret material.

### Phase 6: unified administration UI

- [ ] Add the top-level Syncs and Connections tabs.
- [ ] Move virtual-share management under Shares.
- [ ] Add Local Shares and Virtual Shares tabs or an equivalent accessible type filter.
- [ ] Add connection usage, capabilities, health, and reauthorization UI.
- [ ] Make create actions connection-first.
- [ ] Add every label and message to English and German resource files.

Exit criterion: an administrator authorizes a connection once and can create supported syncs and virtual shares from it without repeating provider authorization.

### Phase 7: protocol providers

- [ ] Add SMB with strict transport and identity validation.
- [ ] Add rsync/SSH with pinned host identity and secret references.
- [ ] Add NFS only through an operator-managed mount or isolated helper design.
- [ ] Add provider capability contract tests.
- [ ] Verify that unsupported UI operations remain hidden and backend-enforced.

Exit criterion: adding a provider does not add provider-specific conditionals to generic Syncs, Connections, Shares, or File Browser pages.

### Phase 8: hardening and irreversible cleanup

- [ ] Complete penetration-oriented OAuth callback and token-storage tests.
- [ ] Test key loss, key rotation, token revocation, tenant policy changes, and multi-instance races.
- [ ] Verify audit retention and secret redaction.
- [ ] Remove legacy Cloud Sync credential fields and compatibility readers.
- [ ] Remove obsolete Cloud Access naming from domain and routes after redirects are in place.
- [ ] Publish backup, restore, credential rotation, and disaster-recovery runbooks.

Exit criterion: legacy code cannot serialize a provider credential into a share, and rollback relies on documented database/application backup rather than dual credential storage.

## 19. Test requirements

### Credential tests

- ciphertext changes when context or key version changes;
- ciphertext copied between connections cannot be decrypted;
- secrets never appear in serialization, logs, API responses, exception pages, or snapshots;
- rotated refresh tokens are not lost during concurrent use;
- key rotation supports old reads and new writes;
- backup/restore preserves decryptability;
- key loss produces an explicit non-destructive recovery state.

### OAuth tests

- state, nonce, PKCE verifier, redirect URI, actor, connection, and expiry validation;
- callback replay is rejected;
- authorization transaction is rejected after the connection is deleted or ownership changes;
- provider account or tenant substitution is rejected;
- denied consent and terminal grant errors lead to safe states;
- transient provider errors do not discard credentials;
- device polling obeys interval and `slow_down`;
- error bodies and tokens are redacted.

### Authorization tests

- share permissions alone cannot use an unauthorized connection;
- connection permissions alone cannot create a sync on an unauthorized local share;
- cross-department reuse is denied without an explicit grant;
- users with `UseConnections` cannot export credentials;
- reauthorization and deletion are independently permission-checked.

### Migration tests

- every legacy sync is migrated exactly once;
- interruption and restart are idempotent;
- source checksums prevent silent partial migration;
- no automatic connection deduplication occurs;
- legacy plaintext credentials are cleared only after validated cutover;
- rollback before cleanup leaves a usable legacy record.

## 20. Operational acceptance criteria

An enterprise-ready release must demonstrate:

1. a fresh Docker installation automatically creates safe local protection material;
2. restarting the stack preserves connection decryptability;
3. restoring database and application key material restores connections;
4. a customer-owned Microsoft tenant profile works with tenant restrictions and certificate/workload identity configuration;
5. a customer-owned Google profile works without any secret embedded in the image;
6. non-interactive Microsoft and Google Workspace modes require no recurring end-user sign-in after administrator setup;
7. revoked consent becomes `NeedsReauthorization` without deleting sync/share configuration;
8. multiple syncs and virtual shares reuse one connection safely;
9. deletion is blocked while the connection has consumers;
10. no secret appears in logs collected at the most verbose supported production level.

## 21. Open decisions

These decisions should be made before their corresponding phase begins:

- final UI name: **Cloud Access**, **External Storage**, or **Storage Connections**;
- whether a common `Share` base table is introduced during Phase 6 or the unified catalog remains a read model initially;
- which external vault/KMS implementation is supported first;
- whether Kaimo will ever operate a vendor-managed Google OAuth broker;
- whether Google `drive.file` plus Google Picker is sufficient for a limited virtual-share mode;
- which Microsoft Selected permission level is practical for the target SharePoint/OneDrive resource types;
- whether service-identity modes are part of the first consolidated provider release or a following enterprise milestone;
- audit retention duration and export format.

The Microsoft default authorization choice is not open: the shipped public client ID plus device-code flow remains the zero-configuration default, with optional installation-local environment overrides added later.

## 22. Official references

Microsoft:

- [Microsoft identity platform authorization-code flow with PKCE](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
- [Microsoft identity platform device authorization grant](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Conditional Access controls for authentication flows](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-authentication-flows)
- [Use certificates with Microsoft.Identity.Web](https://learn.microsoft.com/en-us/entra/msidweb/authentication/certificates)
- [Microsoft Graph application access without a user](https://learn.microsoft.com/en-us/graph/auth-v2-service)
- [Selected permissions for OneDrive and SharePoint](https://learn.microsoft.com/en-us/graph/permissions-selected-overview)
- [OneDrive API permission scopes](https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference?view=odsp-graph-online)

Google:

- [OAuth 2.0 for web-server applications](https://developers.google.com/identity/protocols/oauth2/web-server)
- [Google OAuth 2.0 policies](https://developers.google.com/identity/protocols/oauth2/policies)
- [Google OAuth 2.0 best practices](https://developers.google.com/identity/protocols/oauth2/resources/best-practices)
- [Google Drive API scope selection](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [OAuth app verification](https://support.google.com/cloud/answer/13463073)
- [Server-to-server OAuth and Domain-Wide Delegation](https://developers.google.com/identity/protocols/oauth2/service-account)
- [Service-account security best practices](https://cloud.google.com/iam/docs/best-practices-service-accounts)
