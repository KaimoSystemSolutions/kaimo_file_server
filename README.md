# 🐢 Kaimo File Server

The Kaimo File Server is a self-hosted file server, which allows you to use the file server however you like.
It is a Docker Compose solution, which is meant to run on a VM / LXC of a hypervisor like Proxmox for example.

⚠️ Databases created by a beta release can **not** be upgraded to 1.0 — start 1.0 with an empty database.
As with any file server, keep regular backups of sensitive data.



## ✨ Features

### 📁 Storage
- Multiple custom storage pools, with different mount points
- Shares are based on one of these storage pools
- Virtual shares to access external storage from the built-in filebrowser

### 🔄 Synchronization
- You can sync files between local shares and an external storage connection

### ☁︎ External storage
- SMB
- RSYNC
- WebDAV
- SFTP
- OneDrive
- Dropbox

### 👥 Access Control
- Per-share and per-folder ACLs (ACLs are NTFS like)
- User, group and department permissions
- Custom roles
- Departments which can handle scoped access to resources

### 🔍 Search
- In-text file search powered by Elasticsearch (if RAM is short, just don't use the ES container, we have a fallback)

### ✉️ Notifications
- Can be sent over E-Mail
- Live design preview of the message 
- Editable mail templates per language
  
### 🔄 Coming soon: Syncing to your local devices on
- Windows
- Linux
- macOS
- Android
- iOS

## How to run it

Under [/example](/example) you can find more information about it.

## 📜 License

Kaimo File Server is open source under the **GNU Affero General Public License
v3.0 or later** ([`LICENSE`](LICENSE)). You may use, modify and redistribute it;
if you modify it and offer it to others — including over a network as a service —
you must make your modified source available under the same license.

Exceptions and details are in [`NOTICE`](NOTICE):

- **`src/samba-vfs/`** is **GPL-3.0-or-later** (it is a Samba-derived module),
  see [`src/samba-vfs/LICENSE`](src/samba-vfs/LICENSE).
- Bundled NuGet dependencies are permissive (MIT / Apache-2.0 / BSD / PostgreSQL).
  Their license texts are
  generated into [`third-party/nuget/`](third-party/nuget/) and shipped in each
  .NET image as `/usr/share/doc/kaimo/NUGET_THIRD_PARTY_LICENSES.txt`.
- The Web image ships the unmodified **restic** binary (BSD-2-Clause) for file
  backups; its license and those of the Go modules compiled into it are in
  [`third-party/restic/`](third-party/restic/).
- The Samba image ships a self-built, patched **Samba** (GPL-3.0-or-later); see
  [`src/samba-vfs/SOURCE.txt`](src/samba-vfs/SOURCE.txt) for its source.
- All images contain Ubuntu, PGDG or Alpine **OS packages** (some GPL/LGPL);
  each image lists them in `/usr/share/doc/kaimo/os-packages.txt`, and NOTICE
  says where their source is available.

> **Commercial use:** a separate commercial license is available from the copyright
holder for those who cannot meet the AGPL obligations or want to build
proprietary modules. Contributions are accepted under a Contributor License
Agreement so the project can continue to be offered under both licenses.


## Why I built this

I have been running a Synology for a long time and tried to switch to TrueNAS, but I didn't get what I expected. 
The best case for TrueNAS is to attach your drives directly to the VM, which didn't make sense for me, because I wanted to use a hypervisor for all of my stuff and not only some parts of it. 
I searched for a long time, but I didn't find anything I wanted to work with and trust with all my sensitive files.
So I thought I could just build it myself: a file server that fits my needs best. 
