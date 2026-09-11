# WebDAV — Connection Guide

The server exposes a WebDAV endpoint at **`/dav`** on the same HTTPS port as the
web UI. It is a second file transport alongside SMB: the same shares, the same
per-item permissions, the same recycle bin and versioning. Enable it on the
**Settings → Data services** page (it is off by default).

## Address form

A share is addressed by its **name**, exactly as over SMB:

| SMB | WebDAV |
|---|---|
| `\\host\Projekte` | `https://host:8443/dav/Projekte` |

The root `https://host:8443/dav/` lists the shares you may access.

## TLS is required for Basic authentication

Desktop clients authenticate with HTTP Basic. The server refuses Basic over
plain HTTP and answers `426 Upgrade Required`, so **always use the `https://`
address**. If a reverse proxy terminates TLS in front of the server, either keep
the HTTPS requirement and have the proxy forward `X-Forwarded-Proto`, or turn off
*Require HTTPS for Basic authentication* on the settings page for that
deployment. Hosting `/dav` on a separate hostname from the web UI is recommended
but not required.

## Clients

### Windows Explorer (Map network drive)

Use **Map network drive** and enter `https://host:8443/dav/Projekte`, or from a
command prompt:

```
net use * https://host:8443/dav/Projekte /user:alice
```

The built-in **WebClient** service has two registry values that commonly need
adjusting (restart the *WebClient* service afterwards):

| Value | Path | Why |
|---|---|---|
| `BasicAuthLevel` = `2` | `HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters` | Allow Basic authentication over HTTPS (default `1` permits it only for SSL connections; `2` is explicit). |
| `FileSizeLimitInBytes` = `4294967295` | `HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters` | Raise the 50 MB default download cap (max ~4 GB). |

### macOS Finder

**Go → Connect to Server…**, enter `https://host:8443/dav/Projekte`.

### Linux (davfs2 / GVfs)

```
sudo mount -t davfs https://host:8443/dav/Projekte /mnt/projekte
```

### rclone, Cyberduck, WinSCP

Configure a WebDAV remote with the base URL `https://host:8443/dav/`, Basic
authentication, and your username and password.

## Not supported

Shared locks, `Depth: infinity` PROPFIND, quota properties, the ACL protocol
(RFC 3744) and SEARCH/DASL are intentionally not implemented — no supported
client requires them. WebDAV locks are per-process and do not coordinate with
SMB locks held by the Samba server, exactly as the web UI does not today.
