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
STATE_DIR="${KAIMO_SYNC_STATE_DIR:-/var/lib/kaimo-user-sync}"
MANAGED_STATE="$STATE_DIR/managed-users"
UNMANAGED_USERS="${KAIMO_UNMANAGED_SAMBA_USERS:-${KAIMO_TEST_USER:-kaimotest}}"
SMBPASSWD=""
AUTH_ERR=""
PDBEDIT_LOG=""
DESIRED_USERS=""
LOCAL_PASSDB_USERS=""
PREVIOUS_MANAGED=""
STATE_TMP=""

cleanup() {
    [ -n "$SMBPASSWD" ] && rm -f -- "$SMBPASSWD"
    [ -n "$AUTH_ERR" ] && rm -f -- "$AUTH_ERR"
    [ -n "$PDBEDIT_LOG" ] && rm -f -- "$PDBEDIT_LOG"
    [ -n "$DESIRED_USERS" ] && rm -f -- "$DESIRED_USERS"
    [ -n "$LOCAL_PASSDB_USERS" ] && rm -f -- "$LOCAL_PASSDB_USERS"
    [ -n "$PREVIOUS_MANAGED" ] && rm -f -- "$PREVIOUS_MANAGED"
    [ -n "$STATE_TMP" ] && rm -f -- "$STATE_TMP"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

prepare_private_directory() {
    local directory="$1"
    if [ -L "$directory" ]; then
        echo "[sync-users] Refusing symlink private directory: $directory"
        return 1
    fi
    if [ ! -e "$directory" ]; then
        mkdir -m 0700 -- "$directory" || {
            echo "[sync-users] Cannot create private directory: $directory"
            return 1
        }
    fi
    if [ ! -d "$directory" ] \
        || [ "$(stat -c '%u' -- "$directory" 2>/dev/null)" != "$(id -u)" ] \
        || [ "$(stat -c '%a' -- "$directory" 2>/dev/null)" != "700" ]; then
        echo "[sync-users] Private directory must be owned by the current user with mode 0700: $directory"
        return 1
    fi
}

publish_managed_state() {
    local source="$1"
    STATE_TMP="$(mktemp "$STATE_DIR/managed-users.XXXXXX")" || return 1
    cp -- "$source" "$STATE_TMP" || return 1
    chmod 0600 "$STATE_TMP" || return 1
    mv -f -- "$STATE_TMP" "$MANAGED_STATE" || return 1
    STATE_TMP=""
}

is_unmanaged_user() {
    local candidate="$1"
    local item
    local old_ifs="$IFS"
    IFS=','
    for item in $UNMANAGED_USERS; do
        if [ "$candidate" = "$item" ]; then
            IFS="$old_ifs"
            return 0
        fi
    done
    IFS="$old_ifs"
    return 1
}

remove_secondary_group() {
    local user="$1"
    local group="$2"
    if id -nG "$user" 2>/dev/null | tr ' ' '\n' | grep -Fqx "$group"; then
        gpasswd -d "$user" "$group" >/dev/null 2>&1 || {
            echo "[sync-users] Cannot remove stale user '$user' from group '$group'."
            return 1
        }
    fi
}

# Create the directory with the invoking identity and reject an existing
# symlink, foreign owner, or group/other access. mktemp below then provides
# O_EXCL-style unpredictable file creation inside this trusted directory.
umask 077
prepare_private_directory "$RUNTIME_DIR" || exit 1
prepare_private_directory "$STATE_DIR" || exit 1

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
DESIRED_USERS="$(mktemp "$RUNTIME_DIR/desired-users.XXXXXX")" || exit 1
LOCAL_PASSDB_USERS="$(mktemp "$RUNTIME_DIR/local-users.XXXXXX")" || exit 1
PREVIOUS_MANAGED="$(mktemp "$RUNTIME_DIR/managed-users.XXXXXX")" || exit 1

# Capture local passdb state before importing. On the first P1-16 run, existing
# tdbsam users are adopted as Kaimo-managed except for explicitly reserved
# accounts. Later runs use the private state file and never touch unrelated
# accounts introduced after that bootstrap.
if ! pdbedit -L >"$PDBEDIT_LOG" 2>&1; then
    echo "[sync-users] Cannot enumerate local tdbsam users."
    exit 1
fi
cut -d: -f1 "$PDBEDIT_LOG" | sort -u >"$LOCAL_PASSDB_USERS"

if [ -e "$MANAGED_STATE" ] || [ -L "$MANAGED_STATE" ]; then
    if [ -L "$MANAGED_STATE" ] || [ ! -f "$MANAGED_STATE" ] \
        || [ "$(stat -c '%u' -- "$MANAGED_STATE" 2>/dev/null)" != "$(id -u)" ] \
        || [ "$(stat -c '%a' -- "$MANAGED_STATE" 2>/dev/null)" != "600" ]; then
        echo "[sync-users] Managed-user state must be a current-user-owned mode-0600 regular file."
        exit 1
    fi
    cp -- "$MANAGED_STATE" "$PREVIOUS_MANAGED" || exit 1
else
    while IFS= read -r local_user; do
        [ -z "$local_user" ] && continue
        is_unmanaged_user "$local_user" && continue
        printf '%s\n' "$local_user" >>"$PREVIOUS_MANAGED"
    done <"$LOCAL_PASSDB_USERS"
    # Persist the adopted ownership boundary before any destructive mutation.
    # If a first reconciliation fails halfway through, its retry must still
    # know which passdb/group identities remain to be converged.
    publish_managed_state "$PREVIOUS_MANAGED" || {
        echo "[sync-users] Cannot publish initial managed-user state."
        exit 1
    }
fi

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
            || useradd -M -s /usr/sbin/nologin "$user" 2>/dev/null || {
                echo "[sync-users] Cannot create POSIX identity for '$user'."
                exit 1
            }
    fi
    # Into the shared storage group (write permission on shares), WITHOUT touching
    # per-user identity: the primary UID/group stays, kaimo is added
    # only as secondary group. The entrypoint creates the group.
    usermod -aG "${KAIMO_STORAGE_GROUP:-kaimo}" "$user" 2>/dev/null || {
        echo "[sync-users] Cannot add '$user' to the storage group."
        exit 1
    }
    # P1-04: membership grants traversal/connect to the private authd socket.
    # authd still verifies SO_PEERCRED, the smbd executable, and this user's UID.
    usermod -aG "${KAIMO_AUTHD_GROUP:-kaimo-authd}" "$user" 2>/dev/null || {
        echo "[sync-users] Cannot add '$user' to the authd group."
        exit 1
    }
    uid="$(id -u "$user" 2>/dev/null)" || {
        echo "[sync-users] Cannot resolve POSIX identity for '$user'."
        exit 1
    }
    printf '%s:%s:%s:%s:[U          ]:%s:\n' "$user" "$uid" "$LM" "$nthash" "$LCT" >> "$SMBPASSWD"
    printf '%s\n' "$user" >>"$DESIRED_USERS"
    count=$((count + 1))
done <<< "$OUT"
sort -u -o "$DESIRED_USERS" "$DESIRED_USERS"

if [ "$count" -gt 0 ]; then
    if ! pdbedit -i "smbpasswd:$SMBPASSWD" -e tdbsam >"$PDBEDIT_LOG" 2>&1; then
        echo "[sync-users] tdbsam import failed."
        exit 1
    fi
    echo "[sync-users] $count users imported into tdbsam."
else
    echo "[sync-users] no active users received."
fi

# Remove credentials no longer present in the bridge response. POSIX identities
# deliberately remain locked with their UID so existing file ownership is not
# orphaned and the UID cannot be reassigned to a different person.
revoked=0
while IFS= read -r stale_user; do
    [ -z "$stale_user" ] && continue
    is_unmanaged_user "$stale_user" && continue
    grep -Fqx -- "$stale_user" "$DESIRED_USERS" && continue

    if grep -Fqx -- "$stale_user" "$LOCAL_PASSDB_USERS"; then
        if ! pdbedit -x -u "$stale_user" >>"$PDBEDIT_LOG" 2>&1; then
            echo "[sync-users] Cannot remove stale tdbsam credential for '$stale_user'."
            exit 1
        fi
    fi
    if id "$stale_user" >/dev/null 2>&1; then
        remove_secondary_group "$stale_user" "${KAIMO_STORAGE_GROUP:-kaimo}" || exit 1
        remove_secondary_group "$stale_user" "${KAIMO_AUTHD_GROUP:-kaimo-authd}" || exit 1
        usermod -L -s /usr/sbin/nologin "$stale_user" 2>/dev/null || {
            echo "[sync-users] Cannot lock retained POSIX identity '$stale_user'."
            exit 1
        }
    fi
    revoked=$((revoked + 1))
done <"$PREVIOUS_MANAGED"

# Read the passdb back after every mutation. A command returning zero is not
# sufficient evidence that tdbsam reached the desired state.
if ! pdbedit -L >"$PDBEDIT_LOG" 2>&1; then
    echo "[sync-users] Cannot verify reconciled tdbsam users."
    exit 1
fi
cut -d: -f1 "$PDBEDIT_LOG" | sort -u >"$LOCAL_PASSDB_USERS"
while IFS= read -r desired_user; do
    [ -z "$desired_user" ] && continue
    if ! grep -Fqx -- "$desired_user" "$LOCAL_PASSDB_USERS"; then
        echo "[sync-users] Verification failed: missing tdbsam user '$desired_user'."
        exit 1
    fi
    for desired_group in "${KAIMO_STORAGE_GROUP:-kaimo}" "${KAIMO_AUTHD_GROUP:-kaimo-authd}"; do
        if ! id -nG "$desired_user" 2>/dev/null | tr ' ' '\n' | grep -Fqx "$desired_group"; then
            echo "[sync-users] Verification failed: '$desired_user' is not in '$desired_group'."
            exit 1
        fi
    done
done <"$DESIRED_USERS"
while IFS= read -r stale_user; do
    [ -z "$stale_user" ] && continue
    is_unmanaged_user "$stale_user" && continue
    grep -Fqx -- "$stale_user" "$DESIRED_USERS" && continue
    if grep -Fqx -- "$stale_user" "$LOCAL_PASSDB_USERS"; then
        echo "[sync-users] Verification failed: stale tdbsam user remains '$stale_user'."
        exit 1
    fi
done <"$PREVIOUS_MANAGED"

# Publish the new ownership boundary only after import and revocation complete.
# A failed run therefore leaves the prior state available for an idempotent retry.
publish_managed_state "$DESIRED_USERS" || {
    echo "[sync-users] Cannot publish reconciled managed-user state."
    exit 1
}

[ "$revoked" -gt 0 ] && echo "[sync-users] $revoked stale users revoked; POSIX UIDs retained."
exit 0
