#!/bin/bash
# Fetches active users + NT-Hash from Kaimo bridge (via kaimo_authsync)
# and imports them into Samba's tdbsam so NTLMv2 logins are verified locally
# against the real Kaimo NT-Hash. Idempotent.
#
# Exit 0 only on successful retrieval (for retry loop in entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_authsync 2>>/tmp/authsync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-users] Bridge unreachable (rc=$rc) - see /tmp/authsync.err"
    exit 1
fi

SMBPASSWD=/tmp/kaimo.smbpasswd
: > "$SMBPASSWD"
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
    pdbedit -i "smbpasswd:$SMBPASSWD" -e tdbsam >/tmp/pdbedit.log 2>&1
    echo "[sync-users] $count users imported into tdbsam."
else
    echo "[sync-users] no active users received."
fi
exit 0
