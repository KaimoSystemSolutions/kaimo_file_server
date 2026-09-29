# 🐢 Kaimo File Server

The Kaimo File Server is a selfhosted fileserver, which allows you to use the fileserver how you like it.
It is a docker compose solutions, which is meant to be running on a VM / LXC of a hypervisor like Proxmox for example.

🚧 Kaimo File Server is still under developement, make backups if you use it with sensible data. 
Please feel free to test the active beta and let me know your feedback on it.

## ✨ Features

### 📁 Storage
- Multiple custom storage pools, with differnt mountpoints
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
- In-text file search powered by Elasticsearch (if ram is short, just use the regular filename search)

### ✉️ Notifications
- Can be sent over E-Mail
- Live design preview of the message 
- Editable mail templates per language
  
### 🔄 Coming soon: Syncing to your local devices on
- Windows
- Linux
- MacOS
- Android
- IOS

## How to run it

Under [/example](/example) you find more infos about it

## 📜 License

Kaimo File Server is open source under the **GNU Affero General Public License
v3.0 or later** ([`LICENSE`](LICENSE)). You may use, modify and redistribute it;
if you modify it and offer it to others — including over a network as a service —
you must make your modified source available under the same license.

Exceptions and details are in [`NOTICE`](NOTICE):

- **`src/samba-vfs/`** is **GPL-3.0-or-later** (it is a Samba-derived module),
  see [`src/samba-vfs/LICENSE`](src/samba-vfs/LICENSE).
- Bundled NuGet dependencies are permissive (MIT / Apache-2.0 / BSD / PostgreSQL).

> **Commercial use:** a separate commercial license is available from the copyright
holder for those who cannot meet the AGPL obligations or want to build
proprietary modules. Contributions are accepted under a Contributor License
Agreement so the project can continue to be offered under both licenses.


## Why I built this

I have been running a synology for a long time, tried to switch to TrueNas but I didn't got what I expected. 
The best case for TrueNAS is to directly attach your drives to the VM, which doesn't made sense for me, because I wanted to use a hypervisor for all of my stuff and not only some parts of it. 
I've searched a long time but I didn't found something I wanted to work with and to trust my whole sensitive files on.
So I thought I just could do it by myself, just a fileserver which fits my needs the most. 
