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

while IFS=$'\t' read -r user nthash; do
    [ -z "${user:-}" ] && continue
    # POSIX-User anlegen (Samba passdb braucht getpwnam). Punkte im Namen erlauben.
    if ! id "$user" >/dev/null 2>&1; then
        useradd --badnames -M -s /usr/sbin/nologin "$user" 2>/dev/null \
            || useradd -M -s /usr/sbin/nologin "$user" 2>/dev/null || true
    fi
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
