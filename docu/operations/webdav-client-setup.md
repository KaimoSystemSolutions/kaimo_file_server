# WebDAV Client Setup

How to connect WebDAV clients to Kaimo File Server. The server side is described in
[WebDAV server](../external-access/webdav.md). The service is disabled by default and is switched on with
the data-service setting `services.webdav.enabled`.

## Address form

A share is addressed by its name, exactly as over SMB:

| SMB | WebDAV |
|---|---|
| `\\host\Projects` | `https://host:8443/dav/Projects` |

The root `https://host:8443/dav/` lists the shares the user may access.

## TLS and reverse proxies

Desktop clients authenticate with HTTP Basic, which the server refuses over plain HTTP
(`426 Upgrade Required`). Always use the `https://` address.

With a TLS-terminating reverse proxy, either keep the HTTPS requirement and let the proxy send
`X-Forwarded-Proto`, or turn off `services.webdav.requireHttps` for that deployment. Forwarded headers
are honored only from trusted peers, by default loopback only. List the proxy explicitly — also one in
the same Docker network:

```env
ForwardedHeaders__KnownProxies__0=<proxy-ip>
# or
ForwardedHeaders__KnownNetworks__0=<cidr>
```

Setting either replaces the default. The forwarded client address also keys the login lockout, so an
untrusted proxy makes all clients share one lockout counter. Hosting `/dav` on a separate hostname
from the web UI is recommended.

## Clients

### Windows Explorer

Map a network drive to `https://host:8443/dav/Projects`, or:

```bat
net use * https://host:8443/dav/Projects /user:alice
```

Two WebClient registry values under `HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters`
commonly need adjusting (restart the WebClient service afterwards):

| Value | Purpose |
|---|---|
| `BasicAuthLevel` = `2` | Allow Basic authentication over HTTPS explicitly |
| `FileSizeLimitInBytes` = `4294967295` | Raise the 50 MB default download limit (maximum about 4 GB) |

### macOS Finder

Go → Connect to Server…, enter `https://host:8443/dav/Projects`.

### Linux (davfs2 / GVfs)

```bash
sudo mount -t davfs https://host:8443/dav/Projects /mnt/projects
```

### rclone, Cyberduck, WinSCP

Configure a WebDAV remote with base URL `https://host:8443/dav/`, Basic authentication, username and
password.
