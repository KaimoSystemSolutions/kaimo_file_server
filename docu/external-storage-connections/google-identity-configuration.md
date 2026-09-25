# Google Drive Identity Configuration

Package 4 replaces Kaimo's manual Google token exchange with the maintained Google .NET OAuth client. Delegated authorization uses PKCE, an opaque single-use state value, and a protected server-side callback context stored in PostgreSQL. The callback URI, initiating user, department, share, local path, scope profile, and PKCE verifier are bound to the transaction.

## Delegated OAuth

> **Currently not wired up.** The legacy per-share endpoints `/api/google/connect`
> and `/api/google/callback` were removed: they were unreachable (the UI never
> issued tickets for them and a browser redirect carries no API bearer token) and
> wrote refresh tokens in clear text into `share_definitions`. `GoogleOAuthService`
> and this configuration remain for a future first-class Google connection flow;
> until then Google Drive is available through a Workspace service account.

Create a Web application OAuth client in the installation owner's Google Cloud project. Register the exact HTTPS callback URI shown by the configured external base URL:

```text
https://files.example.com/api/google/callback
```

### Why a client ID alone is not accepted

Kaimo's callback is a server-side HTTPS endpoint. Google's web-server OAuth flow
therefore requires a Web application client and its client secret. PKCE protects
the authorization code but does not turn that Web client into a public client or
replace Google's client authentication requirement.

An installed/desktop client can omit a client secret, but Google restricts its
redirect to a custom application scheme or a loopback address on the user's
device. That redirect model cannot safely return authorization to a remote Kaimo
server. Google's limited-input device flow is also not a replacement for full
Drive sync: it supports only a limited scope set and its token request still uses
the registered client authentication values.

Consequently, Kaimo deliberately rejects a Google delegated configuration that
contains only a client ID. Secretless Google deployments must use a supported
workload identity or Workspace service identity when that first-class connection
mode is enabled. A public identifier is never silently treated as proof of the
application's identity.

Mount the client secret as a read-only Docker or Kubernetes secret and configure:

```text
ExternalStorage__Google__ClientId=<customer-owned OAuth client ID>
ExternalStorage__Google__ClientSecretFile=/run/secrets/google_oauth_client_secret
ExternalStorage__Google__ExternalBaseUrl=https://files.example.com/
ExternalStorage__Google__DefaultScopeProfile=ReadWrite
```

`ExternalBaseUrl` is authoritative. Kaimo does not construct OAuth callback URIs from forwarded request headers. HTTPS is required except for an explicit loopback development URL.

For transitional deployments, `ExternalStorage__Google__ClientSecret` and the former `GoogleOAuth__ClientId` / `GoogleOAuth__ClientSecret` environment variables remain readable. They have no tracked defaults. `ClientSecretFile` is the recommended container configuration and cannot be combined with a direct secret value.

## Scope profiles

Kaimo accepts three explicit profiles:

| Configuration value | Google scope | Effective behavior |
| --- | --- | --- |
| `SelectedItems` | `drive.file` | Read/write access only to items created by or explicitly opened/shared with the application |
| `ReadOnly` | `drive.readonly` | Full Drive browsing and download; Kaimo blocks write operations before contacting Google |
| `ReadWrite` | `drive` | Full Drive browsing and synchronization |

The full-Drive profiles use restricted Google scopes. The deployment owner remains responsible for Google OAuth verification, Workspace policy, and any required security assessment.

## Workspace service identity

An operator may mount a Google service-account credential and optionally configure a Workspace user for Domain-Wide Delegation:

```text
ExternalStorage__Google__Workspace__CredentialFile=/run/secrets/google_workspace_service_account
ExternalStorage__Google__Workspace__ImpersonatedSubject=sync-user@example.com
```

The credential file must be an absolute path and contain a service-account JSON credential. Kaimo validates its structure without logging its content. Domain-Wide Delegation and the relevant Drive scopes must be approved by a Workspace administrator before impersonation can succeed.

The runtime includes a dedicated factory that creates scoped credentials through Google's credential factory. It is intentionally not selectable through legacy share JSON: doing so would allow a share configuration to claim a deployment-wide identity without the future connection authorization checks. The administration workflow that activates it arrives with Package 5's first-class `StorageConnection` and `SyncDefinition` migration. Operators must not place service-account JSON or private keys in legacy share settings.

Workload Identity Federation remains the preferred future non-key deployment mode. Package 4 deliberately does not claim that a mounted service-account key and a federated workload identity have identical lifecycle behavior.

## Security and migration notes

- The browser receives only the opaque authorization transaction token. It never receives the PKCE verifier, client secret, authorization code after callback processing, refresh token, or access token.
- Authorization callback context is encrypted with ASP.NET Core Data Protection and is atomically consumed across Web instances.
- Provider response bodies are not returned to the browser or written by the controller.
- Newly issued legacy sync entries record the scope profile and authorization mode, but their refresh grant remains in legacy share JSON until Package 5 can migrate it atomically to an encrypted `StorageConnection`. This compatibility limitation remains a release blocker, not an accepted final state.
