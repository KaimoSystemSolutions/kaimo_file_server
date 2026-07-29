#!/bin/bash
# Phase-0-Spike self-test (to be run in the running container via `docker exec`).
# Proves: (1) smbd running, (2) registry share LIVE without reload, (3) SMB read/write.
set -uo pipefail

# Self-built Samba (if present) takes precedence over distro Samba.
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

AUTH_FILE="${KAIMO_SELFTEST_AUTH_FILE:-}"
STORAGE="${KAIMO_STORAGE:-/data/storage}"

line() { printf '\n=== %s ===\n' "$1"; }

[ -n "$AUTH_FILE" ] || {
    echo "[selftest] KAIMO_SELFTEST_AUTH_FILE is required." >&2
    exit 1
}
if [ -L "$AUTH_FILE" ] || [ ! -f "$AUTH_FILE" ]; then
    echo "[selftest] Authentication file is missing or unsafe." >&2
    exit 1
fi
auth_mode="$(stat -c '%a' -- "$AUTH_FILE" 2>/dev/null)"
auth_owner="$(stat -c '%u' -- "$AUTH_FILE" 2>/dev/null)"
case "$auth_mode" in ''|*[!0-7]*) auth_mode=invalid ;; esac
if [ "$auth_owner" != "$(id -u)" ] \
    || [ "$auth_mode" = "invalid" ] \
    || [ $((8#$auth_mode & 077)) -ne 0 ]; then
    echo "[selftest] Authentication file must be current-user-owned without group/other access." >&2
    exit 1
fi
USER="$(sed -n 's/^[[:space:]]*username[[:space:]]*=[[:space:]]*//p' "$AUTH_FILE" | head -n1)"
[[ "$USER" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$ ]] || {
    echo "[selftest] Authentication file has no valid username." >&2
    exit 1
}

line "1) Shares BEFORE net conf (only those from smb.conf file)"
smbclient -L localhost -A "$AUTH_FILE" -m SMB3 2>/dev/null | grep -A20 "Sharename" || echo "(none)"

line "2) Add registry share 'spikeshare' LIVE (no reload/restart)"
mkdir -p "$STORAGE/spikeshare"
# Write permissions for SMB user (in real system, ACL layer handles this).
chown "$USER":"$USER" "$STORAGE/spikeshare" 2>/dev/null || chmod 0777 "$STORAGE/spikeshare"
net conf addshare spikeshare "$STORAGE/spikeshare" writeable=y guest_ok=n "Kaimo Phase-0 Spike Share"
echo "-- net conf list --"
net conf list

line "3) Shares AFTER net conf (smbd was NOT restarted)"
smbclient -L localhost -A "$AUTH_FILE" -m SMB3 2>/dev/null | grep -A20 "Sharename"

line "4) Write file via SMB and list again"
echo "hello kaimo phase 0" > /tmp/hello.txt
smbclient //localhost/spikeshare -A "$AUTH_FILE" -m SMB3 \
    -c "put /tmp/hello.txt hello.txt; ls" 2>/dev/null

line "5) File really lies on storage (direct I/O through Samba)"
ls -l "$STORAGE/spikeshare/" && echo "Content:" && cat "$STORAGE/spikeshare/hello.txt"

line "6) Remove share LIVE again"
net conf delshare spikeshare
echo "-- remaining registry shares --"
net conf list | grep '^\[' || echo "(no registry shares left)"

printf '\n[selftest] DONE.\n'
