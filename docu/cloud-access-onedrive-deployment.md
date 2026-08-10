# Microsoft OneDrive Cloud Access — Deployment Guide

## Scope

The first Cloud Access provider maps a selectable OneDrive folder as a virtual share visible only in the web file browser. Directory listings transfer metadata only. File content is retrieved for preview, download, upload, or an explicit copy operation.

Included functionality:

- Microsoft device-code sign-in without a client secret or public callback;
- refresh-token encryption through ASP.NET Data Protection;
- separate connection, virtual-share, and grant records;
- default-deny user and group grants for the complete virtual share;
- optional read-only mode;
- browse, preview, streaming download, upload, create directory, delete, rename, move, and provider-side copy;
- stable Graph item ID for the selected remote root;
- streamed OneDrive-to-local copy with local authorization checks;
- short-lived, single-use download tickets;
- a short process-local directory metadata cache.

## Deployment changes

### 1. Database

Migration `20260810180547_CloudAccessOneDrive` creates:

- `cloud_access_connections`;
- `cloud_access_shares`;
- `cloud_access_grants`.

Web and Host apply pending migrations at startup under the existing PostgreSQL advisory lock. Back up the database before rollout. The migration does not modify existing local-share or Cloud Sync records.

### 2. Microsoft Entra application

Kaimo Files uses the bundled public, multi-tenant client ID and the `common` authority. Neither value can be overridden through `appsettings` or environment variables.

The application registration associated with the bundled client ID must have:

1. public client/device-code flow enabled under **Authentication**;
2. delegated Microsoft Graph permissions `Files.ReadWrite` and `User.Read`;
3. support for work/school accounts from any Entra tenant and personal Microsoft accounts;
4. administrator consent where required by tenant policy.

The runtime additionally requests `offline_access`. No client secret or redirect URI is required.

Microsoft does not allow an application to silently grant its own delegated permissions. The Microsoft user, or an administrator depending on tenant policy, must consent to the displayed scopes.

No OneDrive OAuth environment variables are required. The Web container only needs outbound HTTPS access to:

- `login.microsoftonline.com`;
- `graph.microsoft.com`.

### 3. Token protection

Refresh tokens are never stored in plaintext. The persisted Data Protection key ring is located at:

```text
/data/kaimo-system/.dp-keys
```

`/data/kaimo-system` must be:

- persistent;
- restricted to the application services that require it;
- backed up together with the database;
- restored together with the database;
- shared identically between Web instances if the service is scaled horizontally.

Without the original key ring, existing OneDrive connections cannot be decrypted and must be authorized again.

On first start, the backend automatically creates a dedicated internal RSA certificate under `/data/kaimo-system/.dp-certificate`. New Data Protection keys are encrypted with this certificate. No certificate path, password, mount, or environment variable is required.

The internal certificate is not the HTTPS server certificate. On Linux, its directory and file are restricted to the application user. The certificate and `.dp-keys` directory must be backed up and restored together. Because both live in the same persistent application-data volume, this protects against accidental disclosure of key-ring files in isolation; it is not a separate trust boundary if the complete volume is compromised.

### 4. Directory metadata cache

Virtual-share directory listings are cached in the Web process for 20 seconds by default. The cache stores metadata only, never file content, credentials, or authorization decisions.

Mutating operations and explicit Refresh invalidate the affected share immediately. Administrators can adjust the TTL between 1 and 300 seconds in **Settings → Cloud Access**. The setting is stored in the global database-backed configuration and applies without a restart.

A longer value improves repeated-navigation latency but also increases the maximum time before externally made OneDrive changes appear without an explicit Refresh.

The cache is intentionally process-local. Multiple Web instances do not share cached listings, which is safe because the entries are short-lived and are not security decisions.

### 5. Scaling and reverse proxy

The official Compose topology currently uses one Web instance. Device-code sessions and five-minute download tickets are stored in memory and do not survive a Web restart. Stored connections and virtual shares are unaffected; an interrupted authorization or unused download ticket must simply be started again.

Until these short-lived stores use a distributed cache, horizontal scaling requires sticky routing to the same Web instance. Every instance also needs the same database, Data Protection key ring, and internal key-encryption certificate.

Reverse proxies should:

- avoid buffering complete streaming downloads;
- allow sufficiently long upload and download timeouts;
- forward client disconnects promptly;
- preserve the application's HTTPS and authentication headers.

## Permissions and operation

- The management permission is `ManageCloudAccess`.
- `FullAdmin` includes it automatically during seeding.
- Delegated administrators can manage connections and shares only in authorized departments.
- Regular users see a virtual share only through a direct or group grant.
- A grant applies to the complete virtual share; there is no second Kaimo ACL layer inside it.
- Deleting a connection or virtual share never deletes OneDrive content.
- Provider errors are logged server-side without returning Graph response bodies or tokens to users.

## Rollout sequence

1. Back up PostgreSQL and `/data/kaimo-system`.
2. Verify that the bundled Entra application registration is active and configured correctly.
3. Deploy the new Host and Web images; the migration runs and the internal Data Protection certificate is created automatically at startup.
4. Open **Cloud Access**, authorize OneDrive, select a remote folder, and assign grants.
5. Optionally adjust the metadata TTL under **Settings → Cloud Access**.
6. Test with a regular user: browse, refresh, large download, read-only behavior, and OneDrive-to-local copy.
7. Verify that the virtual share is absent from Samba share enumeration.

## Rollback

The database migration is additive. Older application versions ignore the new tables. Before rolling back, stop new Cloud Access activity and preserve the database plus Data Protection key ring. Removing a virtual-share mapping is not required and would not remove remote data.

## Microsoft references

- [OAuth 2.0 device authorization grant](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Public client applications](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [Microsoft Graph copy driveItem](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0)
