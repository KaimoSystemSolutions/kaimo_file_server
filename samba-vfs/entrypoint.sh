#!/bin/bash
# Phase-0-Spike Entrypoint: Test-User anlegen und smbd im Vordergrund starten.
set -euo pipefail

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_TEST_USER:-kaimotest}"
TEST_PASS="${KAIMO_TEST_PASS:-Passw0rd!}"

mkdir -p "$STORAGE"

# System-User (nur fuer die Samba-Passdb, kein Login-Shell).
if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi

# Samba-User idempotent anlegen/setzen (tdbsam - nur fuer den Spike-Login).
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s -a "$TEST_USER" >/dev/null 2>&1 || \
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s "$TEST_USER" >/dev/null 2>&1 || true

echo "[entrypoint] Konfig-Check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

echo "[entrypoint] Starte smbd (Vordergrund) ..."
exec smbd --foreground --no-process-group --debug-stdout
