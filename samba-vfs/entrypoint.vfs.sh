#!/bin/bash
# Phase-0-Spike Entrypoint fuer die SELBST GEBAUTE Samba unter /opt/samba.
# Modul, smbd und private Libs stammen aus demselben Build -> keine ABI-Mismatches.
set -euo pipefail

export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_TEST_USER:-kaimotest}"
TEST_PASS="${KAIMO_TEST_PASS:-Passw0rd!}"

mkdir -p "$STORAGE"

# --- Storage-Schreibrechte ueber eine gemeinsame Gruppe (Risiko: Storage-Ownership) ---
# Jeder Kaimo-User hat eine EIGENE UID (Per-User-Identitaet/SID), aber die Share-
# Verzeichnisse gehoeren dem Web/Host-Container (gid=$KAIMO_STORAGE_GID, Default 1654).
# Ohne eine geteilte, schreibbare Gruppe koennen SMB-User nur lesen. Loesung: Gruppe =
# diese GID, alle SMB-User rein (sync-users.sh), Share-Dirs g+w + setgid (neue Dateien
# erben die Gruppe). Zusammen mit den create/directory-masks in smb.conf.vfs ist das
# bidirektional (Web <-> SMB). Die internen Dot-Dirs (.dp-keys/.certs) bleiben aussen vor.
STORAGE_GID="${KAIMO_STORAGE_GID:-1654}"
if ! getent group "$STORAGE_GID" >/dev/null 2>&1; then
    groupadd -g "$STORAGE_GID" "${KAIMO_STORAGE_GROUP:-kaimo}" || true
fi
export KAIMO_STORAGE_GROUP="$(getent group "$STORAGE_GID" | cut -d: -f1)"
export KAIMO_STORAGE_GID
find "$STORAGE" -mindepth 1 -maxdepth 1 -type d -not -name '.*' -print0 2>/dev/null |
    while IFS= read -r -d '' d; do
        chgrp -R "$STORAGE_GID" "$d" 2>/dev/null || true
        chmod -R g+rwX "$d"        2>/dev/null || true
        find "$d" -type d -exec chmod g+s {} + 2>/dev/null || true
    done

# Default-ACLs, WENN das Storage-FS sie kann (echtes Linux/ext4 in Prod): dann erben
# NEUE Dateien -- egal von welchem Prozess (Web/Host/SMB) -- automatisch Gruppen-rwx,
# ganz ohne auf die umask der schreibenden Container angewiesen zu sein. Auf drvfs/9p
# (Windows-Dev-Maschine) unterstuetzt das FS keine ACLs -> wir fallen sauber auf die
# create/directory-masks (smb.conf.vfs) + Container-umask (docker-compose) zurueck.
if command -v setfacl >/dev/null 2>&1 && setfacl -m g:"$STORAGE_GID":rwX "$STORAGE" 2>/dev/null; then
    find "$STORAGE" -mindepth 1 -maxdepth 1 -type d -not -name '.*' -print0 2>/dev/null |
        while IFS= read -r -d '' d; do
            setfacl -R  -m g:"$STORAGE_GID":rwX "$d" 2>/dev/null || true
            setfacl -R -d -m g:"$STORAGE_GID":rwX "$d" 2>/dev/null || true
        done
    echo "[entrypoint] Storage: POSIX-ACLs aktiv -> Default-ACL (Gruppe $KAIMO_STORAGE_GROUP rwX) gesetzt."
else
    echo "[entrypoint] Storage: kein ACL-Support (z.B. drvfs/9p) -> Fallback auf create-mask + Container-umask."
fi

if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi
usermod -aG "${KAIMO_STORAGE_GROUP:-kaimo}" "$TEST_USER" 2>/dev/null || true

printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s -a "$TEST_USER" >/dev/null 2>&1 || \
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s "$TEST_USER" >/dev/null 2>&1 || true

echo "[entrypoint] smbd: $(command -v smbd)  ($(smbd --version))"
echo "[entrypoint] VFS-Modul vorhanden?"
find /opt/samba -name 'kaimo_bridge.so' -o -name '*kaimo_bridge*.so' 2>/dev/null | sed 's/^/  /' || true

echo "[entrypoint] Konfig-Check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

# --- Phase 1: NT-Hashes der Kaimo-Benutzer aus der Bridge in tdbsam syncen ---
# Vor dem Start einmal versuchen (mit Retries, bis die Bridge erreichbar ist),
# damit echte Kaimo-Logins sofort funktionieren.
echo "[entrypoint] Initialer NT-Hash-Sync von der Bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-users.sh; then break; fi
    sleep 2
done

# Danach periodisch nachziehen (neue/geaenderte Benutzer, ohne Neustart).
( while true; do sleep 60; /usr/local/bin/sync-users.sh >/dev/null 2>&1 || true; done ) &

# --- Phase 4: Share-Provisioning aus der Kaimo-DB in Sambas Registry (net conf) ---
# Initial einmal mit Retries (bis die Bridge erreichbar ist), damit die Shares
# schon beim ersten Client-Connect stehen. Ersetzt SmbServer.SyncFromDb().
echo "[entrypoint] Initialer Share-Sync von der Bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-shares.sh; then break; fi
    sleep 2
done

# Danach periodisch nachziehen (neue/geaenderte/geloeschte Shares, ohne Neustart).
( while true; do sleep 60; /usr/local/bin/sync-shares.sh >/dev/null 2>&1 || true; done ) &

# --- Phase 4: Protokoll-Settings aus der Kaimo-DB in Sambas globale Registry ---
# Initial VOR dem smbd-Start (mit Retries), damit smbd die Dialekt-Range/Signing/
# Encryption beim ersten Start schon aus der Registry liest. Spiegelt
# SmbServer.LoadProtocolSettings().
echo "[entrypoint] Initialer Protokoll-Settings-Sync von der Bridge ..."
for i in $(seq 1 30); do
    if /usr/local/bin/sync-config.sh; then break; fi
    sleep 2
done

# Danach periodisch nachziehen (Web-UI-Aenderungen, reload nur bei Aenderung).
( while true; do sleep 60; /usr/local/bin/sync-config.sh >/dev/null 2>&1 || true; done ) &

# --- Phase 2: Autorisierungs-Sidecar starten (Unix-Socket <-> gRPC) ---
# Das VFS-Modul (connect-Hook) fragt hier "darf <user> auf <share>?".
mkdir -p /var/run/kaimo
export KAIMO_AUTHD_SOCK="${KAIMO_AUTHD_SOCK:-/var/run/kaimo/authz.sock}"
echo "[entrypoint] Starte kaimo_authd (Authz-Sidecar) ..."
kaimo_authd &
# Kurz auf den Socket warten, damit der erste TREE_CONNECT nicht ins Leere laeuft.
for i in $(seq 1 20); do [ -S "$KAIMO_AUTHD_SOCK" ] && break; sleep 0.2; done

echo "[entrypoint] Starte selbst gebaute smbd (Vordergrund) ..."
exec smbd --foreground --no-process-group --debug-stdout
