#!/bin/bash
# Phase-0-Spike entrypoint: create test user and start smbd in foreground.
set -euo pipefail

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_TEST_USER:-kaimotest}"
TEST_PASS="${KAIMO_TEST_PASS:-Passw0rd!}"

mkdir -p "$STORAGE"

# System user (only for Samba passdb, no login shell).
if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi

# Samba user idempotent create/set (tdbsam - only for spike login).
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s -a "$TEST_USER" >/dev/null 2>&1 || \
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s "$TEST_USER" >/dev/null 2>&1 || true

echo "[entrypoint] Config check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

echo "[entrypoint] Starting smbd (foreground) ..."
exec smbd --foreground --no-process-group --debug-stdout
