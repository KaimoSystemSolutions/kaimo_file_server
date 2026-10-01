# Microsoft OneDrive Setup

OneDrive connections use the OAuth 2.0 device-code flow with a public client: no client secret and
no inbound callback URL are needed. This guide covers the application registration, optional
overrides and runtime requirements. The provider itself is described in
[Providers](../external-storage/providers.md#microsoft-onedrive).

## Default configuration

Without any configuration, Kaimo uses its bundled public, multi-tenant client ID with the `common`
authority and requests the delegated scopes `offline_access Files.ReadWrite User.Read`. A public
client ID is an identifier, not a credential, and is never paired with a client secret.

The application registration behind the client ID must have:

1. public client / device-code flow enabled;
2. delegated Microsoft Graph permissions `Files.ReadWrite` and `User.Read`;
3. support for work/school accounts and, if required, personal Microsoft accounts;
4. administrator consent where tenant policy requires it.

The signing-in user (or a tenant administrator, depending on policy) must consent to the scopes.
Tenant policy and Conditional Access can disallow the device-code flow.

## Using an own Entra application

Organizations that require their own registration override client ID **and** authority together
(`MicrosoftIdentityConfiguration`; setting only one of them fails startup):

```yaml
environment:
  ExternalStorage__Microsoft__PublicClientId: "00000000-0000-0000-0000-000000000000"
  ExternalStorage__Microsoft__Authority: "contoso.onmicrosoft.com"
```

`Authority` may be a tenant domain, a tenant GUID or an authority alias such as `organizations`. Do
not supply a URL, path, query string or client secret; token endpoints are always built below
`https://login.microsoftonline.com`, and invalid values fail startup.

## Runtime requirements

- Outbound HTTPS from the Web container to `login.microsoftonline.com` and `graph.microsoft.com`.
- A persistent `/data/kaimo-system` containing the Data Protection key ring (`.dp-keys/`) and its
  key-encryption certificate (`.dp-certificate/`). Refresh tokens are encrypted with these keys;
  without them existing connections must be authorized again. Back them up and restore them together
  with the database; every Web instance must use the same key ring.
- Device-authorization sessions are stored in the database and survive a Web restart. Download
  tickets and the virtual-share metadata cache are process-local.

## Reverse proxy

For virtual-share downloads and uploads, a reverse proxy should not buffer complete responses, should
allow long transfer timeouts and should forward client disconnects promptly.
