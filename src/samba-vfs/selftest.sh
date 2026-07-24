#!/bin/bash
# Phase-0-Spike self-test (to be run in the running container via `docker exec`).
# Proves: (1) smbd running, (2) registry share LIVE without reload, (3) SMB read/write.
set -uo pipefail

# Self-built Samba (if present) takes precedence over distro Samba.
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

USER="${KAIMO_TEST_USER:-kaimotest}"
PASS="${KAIMO_TEST_PASS:-Passw0rd!}"
CRED="${USER}%${PASS}"
STORAGE="${KAIMO_STORAGE:-/data/storage}"

line() { printf '\n=== %s ===\n' "$1"; }

line "1) Shares BEFORE net conf (only those from smb.conf file)"
smbclient -L localhost -U "$CRED" -m SMB3 2>/dev/null | grep -A20 "Sharename" || echo "(none)"

line "2) Add registry share 'spikeshare' LIVE (no reload/restart)"
mkdir -p "$STORAGE/spikeshare"
# Write permissions for SMB user (in real system, ACL layer handles this).
chown "$USER":"$USER" "$STORAGE/spikeshare" 2>/dev/null || chmod 0777 "$STORAGE/spikeshare"
net conf addshare spikeshare "$STORAGE/spikeshare" writeable=y guest_ok=n "Kaimo Phase-0 Spike Share"
echo "-- net conf list --"
net conf list

line "3) Shares AFTER net conf (smbd was NOT restarted)"
smbclient -L localhost -U "$CRED" -m SMB3 2>/dev/null | grep -A20 "Sharename"

line "4) Write file via SMB and list again"
echo "hello kaimo phase 0" > /tmp/hello.txt
smbclient //localhost/spikeshare -U "$CRED" -m SMB3 \
    -c "put /tmp/hello.txt hello.txt; ls" 2>/dev/null

line "5) File really lies on storage (direct I/O through Samba)"
ls -l "$STORAGE/spikeshare/" && echo "Content:" && cat "$STORAGE/spikeshare/hello.txt"

line "6) Remove share LIVE again"
net conf delshare spikeshare
echo "-- remaining registry shares --"
net conf list | grep '^\[' || echo "(no registry shares left)"

printf '\n[selftest] DONE.\n'
