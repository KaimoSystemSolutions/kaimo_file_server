#!/bin/bash
# Phase-0-Spike Entrypoint fuer die SELBST GEBAUTE Samba unter /opt/samba.
# Modul, smbd und private Libs stammen aus demselben Build -> keine ABI-Mismatches.
set -euo pipefail

export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_TEST_USER:-kaimotest}"
TEST_PASS="${KAIMO_TEST_PASS:-Passw0rd!}"

mkdir -p "$STORAGE"

if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi

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
