# Storage pool custom names

Each storage pool is a directory the server mounts as a share destination — for
example `/mnt/pool01`. That mount path is fixed by the deployment and is the only
value used for file I/O. Administrators can additionally give a pool a friendly
display name (e.g. `SSD_Pool01`) that is shown throughout the UI without touching
the underlying path.

## What a custom name changes

A custom name is purely a label. It affects only what operators see when they pick
or identify a pool:

- The **storage pool dropdown** when creating a share.
- The **Move** dropdown when relocating a share to another pool.
- The **pool column** on the share list and the pool shown in a share's settings.

The persisted share path, the directory on disk, and everything the SMB/file layer
does are unaffected. Renaming a pool never moves data and never rewrites a share's
stored path.

## Assigning a name

Open **Settings → Storage**. Every configured pool card shows its mount path and a
**Custom name** field. Type a name and press **Save**. Leaving the field blank (or
clearing it) falls back to the pool's derived name — the final component of its
path (`/mnt/pool01` → `pool01`). Names are capped at 64 characters and trimmed of
surrounding whitespace.

## How it is stored

Custom names live in the configuration store under the key `storage.poolNames` as a
JSON object mapping the normalized pool path to its display name. Blank entries are
not persisted, so a pool with no custom name carries no row at all.

Resolution is centralized in `StoragePoolNaming` (Core): `Resolve` returns the
custom name when one is set and non-blank, otherwise the path-derived name.
`SettingsViewModel` edits and saves the map; `ShareListViewModel` reads it on load
and rebuilds its pool list so the share and create/move surfaces show the same
labels. Because both view models share the cached config store, a saved name is
visible on the shares page the next time it loads.
