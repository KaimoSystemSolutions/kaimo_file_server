#!/bin/bash
# Phase-0-Spike Selbsttest (im laufenden Container via `docker exec` auszufuehren).
# Beweist: (1) smbd laeuft, (2) Registry-Share LIVE ohne Reload, (3) SMB read/write.
set -uo pipefail

# Selbst gebaute Samba (falls vorhanden) hat Vorrang vor der Distro-Samba.
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

USER="${KAIMO_TEST_USER:-kaimotest}"
PASS="${KAIMO_TEST_PASS:-Passw0rd!}"
CRED="${USER}%${PASS}"
STORAGE="${KAIMO_STORAGE:-/data/storage}"

line() { printf '\n=== %s ===\n' "$1"; }

line "1) Shares VOR net conf (nur die aus smb.conf-Datei)"
smbclient -L localhost -U "$CRED" -m SMB3 2>/dev/null | grep -A20 "Sharename" || echo "(keine)"

line "2) Registry-Share 'spikeshare' LIVE hinzufuegen (kein Reload/Restart)"
mkdir -p "$STORAGE/spikeshare"
# Schreibrechte fuer den SMB-User (im echten System uebernimmt das die ACL-Ebene).
chown "$USER":"$USER" "$STORAGE/spikeshare" 2>/dev/null || chmod 0777 "$STORAGE/spikeshare"
net conf addshare spikeshare "$STORAGE/spikeshare" writeable=y guest_ok=n "Kaimo Phase-0 Spike Share"
echo "-- net conf list --"
net conf list

line "3) Shares NACH net conf (smbd wurde NICHT neu gestartet)"
smbclient -L localhost -U "$CRED" -m SMB3 2>/dev/null | grep -A20 "Sharename"

line "4) Datei ueber SMB schreiben und wieder auflisten"
echo "hallo kaimo phase 0" > /tmp/hello.txt
smbclient //localhost/spikeshare -U "$CRED" -m SMB3 \
    -c "put /tmp/hello.txt hello.txt; ls" 2>/dev/null

line "5) Datei liegt real auf dem Storage (direkte I/O durch Samba)"
ls -l "$STORAGE/spikeshare/" && echo "Inhalt:" && cat "$STORAGE/spikeshare/hello.txt"

line "6) Share LIVE wieder entfernen"
net conf delshare spikeshare
echo "-- verbleibende Registry-Shares --"
net conf list | grep '^\[' || echo "(keine Registry-Shares mehr)"

printf '\n[selftest] FERTIG.\n'
