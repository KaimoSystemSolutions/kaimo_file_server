#!/bin/bash
# Fetches active users + NT-Hash from Kaimo bridge (via kaimo_authsync)
# and imports them into Samba's tdbsam so NTLMv2 logins are verified locally
# against the real Kaimo NT-Hash. Idempotent.
#
# Exit 0 only on successful retrieval (for retry loop in entrypoint).
set -uo pipefail
SAMBA_PATH_PREFIX="${KAIMO_SAMBA_PATH_PREFIX-/opt/samba/sbin:/opt/samba/bin}"
[ -n "$SAMBA_PATH_PREFIX" ] && export PATH="$SAMBA_PATH_PREFIX:$PATH"

# NT hashes must never be written to a shared or predictable temporary path.
# /run is root-owned in the container; tests may select another private parent
# with KAIMO_SYNC_RUNTIME_DIR.
RUNTIME_DIR="${KAIMO_SYNC_RUNTIME_DIR:-/run/kaimo-user-sync}"
SMBPASSWD=""
AUTH_ERR=""
PDBEDIT_LOG=""

cleanup() {
    [ -n "$SMBPASSWD" ] && rm -f -- "$SMBPASSWD"
    [ -n "$AUTH_ERR" ] && rm -f -- "$AUTH_ERR"
    [ -n "$PDBEDIT_LOG" ] && rm -f -- "$PDBEDIT_LOG"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

# Create the directory with the invoking identity and reject an existing
# symlink, foreign owner, or group/other access. mktemp below then provides
# O_EXCL-style unpredictable file creation inside this trusted directory.
umask 077
if [ -L "$RUNTIME_DIR" ]; then
    echo "[sync-users] Refusing symlink runtime directory: $RUNTIME_DIR"
    exit 1
fi
if [ ! -e "$RUNTIME_DIR" ]; then
    mkdir -m 0700 -- "$RUNTIME_DIR" || {
        echo "[sync-users] Cannot create private runtime directory: $RUNTIME_DIR"
        exit 1
    }
fi
if [ ! -d "$RUNTIME_DIR" ] \
    || [ "$(stat -c '%u' -- "$RUNTIME_DIR" 2>/dev/null)" != "$(id -u)" ] \
    || [ "$(stat -c '%a' -- "$RUNTIME_DIR" 2>/dev/null)" != "700" ]; then
    echo "[sync-users] Runtime directory must be owned by the current user with mode 0700: $RUNTIME_DIR"
    exit 1
fi

# Serialize imports so two periodic/startup invocations cannot race over tdbsam.
exec 9>"$RUNTIME_DIR/sync-users.lock" || {
    echo "[sync-users] Cannot open synchronization lock."
    exit 1
}
if ! flock -n 9; then
    echo "[sync-users] Another user synchronization is already running."
    exit 1
fi

SMBPASSWD="$(mktemp "$RUNTIME_DIR/smbpasswd.XXXXXX")" || exit 1
AUTH_ERR="$(mktemp "$RUNTIME_DIR/authsync.XXXXXX")" || exit 1
PDBEDIT_LOG="$(mktemp "$RUNTIME_DIR/pdbedit.XXXXXX")" || exit 1

OUT="$(kaimo_authsync 2>>"$AUTH_ERR")"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-users] Bridge unreachable (rc=$rc)."
    exit 1
fi

LM="XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
LCT="$(printf 'LCT-%08X' "$(date +%s)")"
count=0

# IMPORTANT: Each Kaimo user gets their OWN, distinct UID. Samba derives the
# SID from the UID (rid = 2*uid + base) and does getpwuid lookups; a shared
# UID collapses all users to the same SID/name -> auth/connect breaks.
# (Storage write permission is solved via groups/FS permissions, NOT via
#  shared UIDs or `force user` -- both destroy per-user identity.)
while IFS=$'\t' read -r user nthash; do
    [ -z "${user:-}" ] && continue
    # Create POSIX user (Samba passdb needs getpwnam). Allow dots in names.
    if ! id "$user" >/dev/null 2>&1; then
        useradd --badnames -M -s /usr/sbin/nologin "$user" 2>/dev/null \
            || useradd -M -s /usr/sbin/nologin "$user" 2>/dev/null || true
    fi
    # Into the shared storage group (write permission on shares), WITHOUT touching
    # per-user identity: the primary UID/group stays, kaimo is added
    # only as secondary group. The entrypoint creates the group.
    usermod -aG "${KAIMO_STORAGE_GROUP:-kaimo}" "$user" 2>/dev/null || true
    # P1-04: membership grants traversal/connect to the private authd socket.
    # authd still verifies SO_PEERCRED, the smbd executable, and this user's UID.
    usermod -aG "${KAIMO_AUTHD_GROUP:-kaimo-authd}" "$user" 2>/dev/null || true
    uid="$(id -u "$user" 2>/dev/null)" || continue
    printf '%s:%s:%s:%s:[U          ]:%s:\n' "$user" "$uid" "$LM" "$nthash" "$LCT" >> "$SMBPASSWD"
    count=$((count + 1))
done <<< "$OUT"

if [ "$count" -gt 0 ]; then
    pdbedit -i "smbpasswd:$SMBPASSWD" -e tdbsam >"$PDBEDIT_LOG" 2>&1
    echo "[sync-users] $count users imported into tdbsam."
else
    echo "[sync-users] no active users received."
fi
exit 0
