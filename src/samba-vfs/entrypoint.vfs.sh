#!/bin/bash
# Phase-0-Spike entrypoint for SELF-BUILT Samba under /opt/samba.
# Module, smbd, and private libraries come from the same build -> no ABI mismatches.
set -euo pipefail

export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

STORAGE="${KAIMO_STORAGE:-/data/storage}"

mkdir -p "$STORAGE"
# P1-11 durable outbox. Owner-only permissions are reasserted on every start;
# authd independently verifies ownership, mode, and non-symlink directory type.
install -d -m 0700 -o root -g root \
    "${KAIMO_EVENT_SPOOL_PATH:-/var/lib/kaimo/event-spool}"

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
install -d -m 0755 -o root -g root /var/run/kaimo
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

echo "[entrypoint] smbd: $(command -v smbd)  ($(smbd --version))"
echo "[entrypoint] VFS module present?"
find /opt/samba -name 'kaimo_bridge.so' -o -name '*kaimo_bridge*.so' 2>/dev/null | sed 's/^/  /' || true

echo "[entrypoint] Config check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

# --- P2-13: bounded control-plane convergence and revocation ---
# Polling remains deliberate: these exports are small, bounded desired-state
# documents, while introducing a second push channel would add another durable
# delivery protocol. Every component must converge before smbd starts.
initial_sync() {
    local component="$1" description="$2" command="$3" attempt
    echo "[entrypoint] Initial $description ..."
    for attempt in $(seq 1 30); do
        if /usr/local/bin/run-sync.sh "$component" "$command"; then
            return 0
        fi
        [ "$attempt" -eq 30 ] || sleep 2
    done
    echo "[entrypoint] $component did not converge; refusing to start Samba." >&2
    return 1
}

USER_SYNC_INTERVAL_SECONDS="${KAIMO_USER_SYNC_INTERVAL_SECONDS:-60}"
SHARE_SYNC_INTERVAL_SECONDS="${KAIMO_SHARE_SYNC_INTERVAL_SECONDS:-2}"
CONFIG_SYNC_INTERVAL_SECONDS="${KAIMO_CONFIG_SYNC_INTERVAL_SECONDS:-2}"
# The hash-bearing user export is intentionally limited to two calls per
# 60-second window (P2-10/P2-11). Do not turn it into a high-frequency
# revocation feed; a shorter user SLA requires a separate hash-free endpoint.
/usr/local/bin/validate-sync-interval.sh \
    KAIMO_USER_SYNC_INTERVAL_SECONDS "$USER_SYNC_INTERVAL_SECONDS" 60 60
/usr/local/bin/validate-sync-interval.sh \
    KAIMO_SHARE_SYNC_INTERVAL_SECONDS "$SHARE_SYNC_INTERVAL_SECONDS" 1 5
/usr/local/bin/validate-sync-interval.sh \
    KAIMO_CONFIG_SYNC_INTERVAL_SECONDS "$CONFIG_SYNC_INTERVAL_SECONDS" 1 5

initial_sync users "NT-Hash/user sync from bridge" /usr/local/bin/sync-users.sh
initial_sync shares "share sync from bridge" /usr/local/bin/sync-shares.sh
initial_sync config "protocol settings sync from bridge" /usr/local/bin/sync-config.sh

# A failed cycle closes every active share. If that fail-closed action cannot
# be proven, terminate PID 1; the supervisor then reaps authd/smbd and Compose
# restarts the complete security unit.
start_periodic_sync() {
    local component="$1" interval="$2" command="$3"
    (
        while sleep "$interval"; do
            if ! /usr/local/bin/sync-cycle.sh "$component" "$command"; then
                echo "[entrypoint] Fatal $component revocation failure; terminating Samba unit." >&2
                kill -TERM 1
                exit 1
            fi
        done
    ) &
}

start_periodic_sync users "$USER_SYNC_INTERVAL_SECONDS" /usr/local/bin/sync-users.sh
start_periodic_sync shares "$SHARE_SYNC_INTERVAL_SECONDS" /usr/local/bin/sync-shares.sh
start_periodic_sync config "$CONFIG_SYNC_INTERVAL_SECONDS" /usr/local/bin/sync-config.sh

# --- Phase 2: Start authorization sidecar (Unix socket <-> gRPC) ---
# The VFS module (connect hook) asks here "may <user> access <share>?".
install -d -m 0750 -o root -g "$KAIMO_AUTHD_GROUP" /var/run/kaimo
export KAIMO_AUTHD_SOCK="${KAIMO_AUTHD_SOCK:-/var/run/kaimo/authz.sock}"
echo "[entrypoint] Handing authd and smbd to the fail-fast supervisor ..."
exec /usr/local/bin/supervise-samba.sh
