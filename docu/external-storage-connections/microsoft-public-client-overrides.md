# Microsoft Public-Client Overrides

Package 3 keeps Kaimo's Microsoft device-code flow usable without operator configuration. By default it uses
the public Kaimo application identifier with the `common` Microsoft authority. A public client identifier is
not a credential and must not be paired with a client secret in this flow.

An organization that requires its own Entra application registration can override both settings at deployment
time:

```yaml
environment:
  ExternalStorage__Microsoft__PublicClientId: "00000000-0000-0000-0000-000000000000"
  ExternalStorage__Microsoft__Authority: "contoso.onmicrosoft.com"
```

`Authority` may also be a tenant GUID or a supported Microsoft authority alias such as `organizations`. Do
not supply a URL, path, query string, or client secret. Kaimo always builds token endpoints below the fixed
`https://login.microsoftonline.com` host; invalid values fail startup.

Register the device-code public client and grant the delegated permissions needed by the selected Kaimo
workload. The current default scope is `offline_access Files.ReadWrite User.Read`. Tenant policy and
Conditional Access can still disallow device code; authorization-code with PKCE remains a later Package 3
enterprise mode.
