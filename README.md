# Kaimo File Server

The Kaimo File Server is a selfhosted, feature-rich fileserver designed for granular access control, 
external storage integration, synchronization and custom storage management.

🚧 Kaimo File Server is still under developement.

## Features

### Storage
- Custom storage pools (for example for fast and slow storage)
- Multiple storage pools for different shares
- Virtual shares to access external storage

### Synchronization
- Sync files between local and external storage

### External storage
- SMB
- RSYNC
- WebDAV
- SFTP
- OneDrive
- Dropbox

### Access Control
- Per-share and per-folder ACLs
- User and group permissions
- Custom roles
- Departments and scoped access

### Search
- Elasticsearch-powered file search

### Coming: Syncing to your local device via
- Windows
- Linux
- MacOS
- Android
- IOS

## How to run it

Under /example you find more infos about it


## Why I built this

I have been running a synology for a long time, tried to switch to TrueNas but I didn't got what I expected. So I thought I just could it by myself, but better than all of the other solutions. 
First of all I wanted to have a complete virtual environment, so absolute suitable for a docker deployment on for for example proxmox.
