#!/bin/bash
# Holt aktive Benutzer + NT-Hash von der Kaimo-Bridge (via kaimo_authsync)
# und importiert sie in Sambas tdbsam, sodass NTLMv2-Logins lokal gegen den
# echten Kaimo-NT-Hash geprueft werden. Idempotent.
#
# Exit 0 nur bei erfolgreichem Abruf (fuer den Retry-Loop im Entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_authsync 2>>/tmp/authsync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-users] Bridge nicht erreichbar (rc=$rc) - siehe /tmp/authsync.err"
    exit 1
fi

SMBPASSWD=/tmp/kaimo.smbpasswd
: > "$SMBPASSWD"
LM="XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
LCT="$(printf 'LCT-%08X' "$(date +%s)")"
count=0

# WICHTIG: Jeder Kaimo-User bekommt eine EIGENE, distinkte UID. Samba leitet aus der
# UID u. a. den SID ab (rid = 2*uid + base) und macht getpwuid-Ruecklookups; eine
# geteilte UID kollabiert alle User auf denselben SID/Namen -> Auth/Connect bricht.
# (Das Storage-Schreibrechte-Problem wird ueber Gruppen/FS-Rechte geloest, NICHT ueber
#  geteilte UIDs oder `force user` -- beide zerstoeren die Per-User-Identitaet.)
while IFS=$'\t' read -r user nthash; do
    [ -z "${user:-}" ] && continue
    # POSIX-User anlegen (Samba passdb braucht getpwnam). Punkte im Namen erlauben.
    if ! id "$user" >/dev/null 2>&1; then
        useradd --badnames -M -s /usr/sbin/nologin "$user" 2>/dev/null \
            || useradd -M -s /usr/sbin/nologin "$user" 2>/dev/null || true
    fi
    # In die gemeinsame Storage-Gruppe (Schreibrecht auf die Shares), OHNE die
    # Per-User-Identitaet anzutasten: die eigene primaere UID/Gruppe bleibt, kaimo
    # kommt nur als sekundaere Gruppe dazu. Die Gruppe legt der Entrypoint an.
    usermod -aG "${KAIMO_STORAGE_GROUP:-kaimo}" "$user" 2>/dev/null || true
    uid="$(id -u "$user" 2>/dev/null)" || continue
    printf '%s:%s:%s:%s:[U          ]:%s:\n' "$user" "$uid" "$LM" "$nthash" "$LCT" >> "$SMBPASSWD"
    count=$((count + 1))
done <<< "$OUT"

if [ "$count" -gt 0 ]; then
    pdbedit -i "smbpasswd:$SMBPASSWD" -e tdbsam >/tmp/pdbedit.log 2>&1
    echo "[sync-users] $count Benutzer in tdbsam importiert."
else
    echo "[sync-users] keine aktiven Benutzer erhalten."
fi
exit 0
