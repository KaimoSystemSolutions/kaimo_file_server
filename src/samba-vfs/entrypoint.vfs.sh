#!/bin/bash
# Phase-0-Spike entrypoint for SELF-BUILT Samba under /opt/samba.
# Module, smbd, and private libraries come from the same build -> no ABI mismatches.
set -euo pipefail

export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_TEST_USER:-kaimotest}"
TEST_PASS="${KAIMO_TEST_PASS:-Passw0rd!}"

mkdir -p "$STORAGE"

# --- Storage write permissions via a shared group (risk: storage ownership) ---
# Each Kaimo user has their OWN UID (per-user identity/SID), but share
# directories belong to the Web/Host container (gid=$KAIMO_STORAGE_GID, default 1654).
# Without a shared, writable group, SMB users can only read. Solution: group =
# this GID, all SMB users in it (sync-users.sh), share dirs g+w + setgid (new files
# inherit the group). Together with create/directory masks in smb.conf.vfs, this is
# bidirectional (Web <-> SMB). Internal dot-dirs (.dp-keys/.certs) stay out.
STORAGE_GID="${KAIMO_STORAGE_GID:-1654}"
if ! getent group "$STORAGE_GID" >/dev/null 2>&1; then
    groupadd -g "$STORAGE_GID" "${KAIMO_STORAGE_GROUP:-kaimo}" || true
fi
export KAIMO_STORAGE_GROUP="$(getent group "$STORAGE_GID" | cut -d: -f1)"
export KAIMO_STORAGE_GID

# P1-04: only authenticated smbd workers may reach the local authorization
# sidecar. This group grants connect permission to the private Unix socket; it
# is deliberately separate from the broad storage-write group.
export KAIMO_AUTHD_GROUP="${KAIMO_AUTHD_GROUP:-kaimo-authd}"
if ! getent group "$KAIMO_AUTHD_GROUP" >/dev/null 2>&1; then
    groupadd --system "$KAIMO_AUTHD_GROUP"
fi
find "$STORAGE" -mindepth 1 -maxdepth 1 -type d -not -name '.*' -print0 2>/dev/null |
    while IFS= read -r -d '' d; do
        chgrp -R "$STORAGE_GID" "$d" 2>/dev/null || true
        chmod -R g+rwX "$d"        2>/dev/null || true
        find "$d" -type d -exec chmod g+s {} + 2>/dev/null || true
    done

# Default ACLs, IF the storage filesystem supports them (real Linux/ext4 in prod): then
# NEW files -- regardless of which process (Web/Host/SMB) -- automatically inherit group rwx,
# without depending on the umask of the writing containers. On drvfs/9p
# (Windows dev machine), the filesystem doesn't support ACLs -> we fall back cleanly to
# create/directory masks (smb.conf.vfs) + container umask (docker-compose).
if command -v setfacl >/dev/null 2>&1 && setfacl -m g:"$STORAGE_GID":rwX "$STORAGE" 2>/dev/null; then
    find "$STORAGE" -mindepth 1 -maxdepth 1 -type d -not -name '.*' -print0 2>/dev/null |
        while IFS= read -r -d '' d; do
            setfacl -R  -m g:"$STORAGE_GID":rwX "$d" 2>/dev/null || true
            setfacl -R -d -m g:"$STORAGE_GID":rwX "$d" 2>/dev/null || true
        done
    echo "[entrypoint] Storage: POSIX ACLs active -> default ACL (group $KAIMO_STORAGE_GROUP rwX) set."
else
    echo "[entrypoint] Storage: no ACL support (e.g. drvfs/9p) -> fallback to create-mask + container umask."
fi

if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi
usermod -aG "${KAIMO_STORAGE_GROUP:-kaimo}" "$TEST_USER" 2>/dev/null || true
usermod -aG "$KAIMO_AUTHD_GROUP" "$TEST_USER" 2>/dev/null || true

printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s -a "$TEST_USER" >/dev/null 2>&1 || \
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s "$TEST_USER" >/dev/null 2>&1 || true

echo "[entrypoint] smbd: $(command -v smbd)  ($(smbd --version))"
echo "[entrypoint] VFS module present?"
find /opt/samba -name 'kaimo_bridge.so' -o -name '*kaimo_bridge*.so' 2>/dev/null | sed 's/^/  /' || true

echo "[entrypoint] Config check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

# --- Phase 1: Sync Kaimo user NT-Hashes from bridge into tdbsam ---
# Try once at startup (with retries until bridge is reachable)
# so real Kaimo logins work immediately.
echo "[entrypoint] Initial NT-Hash sync from bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-users.sh; then break; fi
    sleep 2
done

# Then periodically follow up (new/changed users, without restart).
( while true; do sleep 60; /usr/local/bin/sync-users.sh >/dev/null 2>&1 || true; done ) &

# --- Phase 4: Share provisioning from Kaimo DB into Samba registry (net conf) ---
# Initially once with retries (until bridge is reachable), so shares
# are ready at first client connect. Replaces SmbServer.SyncFromDb().
echo "[entrypoint] Initial share sync from bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-shares.sh; then break; fi
    sleep 2
done

# Then continuously reconcile new/changed/deleted shares. Disabled and deleted
# shares are removed from the registry and `smbcontrol close-share` forcibly
# disconnects their active tree connections. Keep this interval short: it is
# the maximum active-handle revocation delay after the database commit.
SHARE_SYNC_INTERVAL_SECONDS="${KAIMO_SHARE_SYNC_INTERVAL_SECONDS:-2}"
case "$SHARE_SYNC_INTERVAL_SECONDS" in
    ''|*[!0-9]*|0)
        echo "[entrypoint] Invalid KAIMO_SHARE_SYNC_INTERVAL_SECONDS='$SHARE_SYNC_INTERVAL_SECONDS' (expected a positive integer)." >&2
        exit 1
        ;;
esac
( while true; do
    sleep "$SHARE_SYNC_INTERVAL_SECONDS"
    /usr/local/bin/sync-shares.sh >/dev/null 2>&1 || true
done ) &

# --- Phase 4: Protocol settings from Kaimo DB into Samba global registry ---
# Initially BEFORE smbd start (with retries), so smbd reads the dialect range/signing/
# encryption from registry at first startup. Mirrors
# SmbServer.LoadProtocolSettings().
echo "[entrypoint] Initial protocol settings sync from bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-config.sh; then break; fi
    sleep 2
done

# Then periodically follow up (web UI changes, reload only on change).
( while true; do sleep 60; /usr/local/bin/sync-config.sh >/dev/null 2>&1 || true; done ) &

# --- Phase 2: Start authorization sidecar (Unix socket <-> gRPC) ---
# The VFS module (connect hook) asks here "may <user> access <share>?".
install -d -m 0750 -o root -g "$KAIMO_AUTHD_GROUP" /var/run/kaimo
export KAIMO_AUTHD_SOCK="${KAIMO_AUTHD_SOCK:-/var/run/kaimo/authz.sock}"
echo "[entrypoint] Starting kaimo_authd (Authz sidecar) ..."
kaimo_authd &
# Wait briefly for the socket, so first TREE_CONNECT doesn't hit empty.
for i in $(seq 1 20); do [ -S "$KAIMO_AUTHD_SOCK" ] && break; sleep 0.2; done

echo "[entrypoint] Starting self-built smbd (foreground) ..."
exec smbd --foreground --no-process-group --debug-stdout
