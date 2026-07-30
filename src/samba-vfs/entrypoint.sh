#!/bin/bash
# Phase-0-Spike entrypoint: create one explicitly configured test user and
# start smbd in foreground. No credential defaults are permitted.
set -euo pipefail

STORAGE="${KAIMO_STORAGE:-/data/storage}"
TEST_USER="${KAIMO_SPIKE_USER:-}"
PASSWORD_FILE="${KAIMO_SPIKE_PASSWORD_FILE:-}"

[ -n "$TEST_USER" ] && [ -n "$PASSWORD_FILE" ] || {
    echo "[entrypoint] KAIMO_SPIKE_USER and KAIMO_SPIKE_PASSWORD_FILE are required." >&2
    exit 1
}
[[ "$TEST_USER" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$ ]] || {
    echo "[entrypoint] KAIMO_SPIKE_USER is invalid." >&2
    exit 1
}
if [ -L "$PASSWORD_FILE" ] || [ ! -f "$PASSWORD_FILE" ]; then
    echo "[entrypoint] Spike password secret is missing or unsafe." >&2
    exit 1
fi
secret_mode="$(stat -c '%a' -- "$PASSWORD_FILE" 2>/dev/null)"
secret_owner="$(stat -c '%u' -- "$PASSWORD_FILE" 2>/dev/null)"
case "$secret_mode" in ''|*[!0-7]*) secret_mode=invalid ;; esac
if [ "$secret_owner" != "$(id -u)" ] \
    || [ "$secret_mode" = "invalid" ] \
    || [ $((8#$secret_mode & 077)) -ne 0 ]; then
    echo "[entrypoint] Spike password secret must be current-user-owned without group/other access." >&2
    exit 1
fi
TEST_PASS=""
IFS= read -r TEST_PASS <"$PASSWORD_FILE" || [ -n "$TEST_PASS" ]
[ -n "$TEST_PASS" ] || {
    echo "[entrypoint] Spike password secret is empty." >&2
    exit 1
}

mkdir -p "$STORAGE"

# System user (only for Samba passdb, no login shell).
if ! id "$TEST_USER" >/dev/null 2>&1; then
    useradd -M -s /usr/sbin/nologin "$TEST_USER"
fi

# Samba user idempotent create/set (tdbsam - only for spike login).
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s -a "$TEST_USER" >/dev/null 2>&1 || \
printf '%s\n%s\n' "$TEST_PASS" "$TEST_PASS" | smbpasswd -s "$TEST_USER" >/dev/null 2>&1
unset TEST_PASS

echo "[entrypoint] Config check (testparm):"
testparm -s 2>/dev/null | sed -n '1,40p' || true

echo "[entrypoint] Starting smbd (foreground) ..."
exec smbd --foreground --no-process-group --debug-stdout
