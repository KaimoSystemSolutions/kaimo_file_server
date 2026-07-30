#!/bin/bash
# Regression for P2-14: operational scripts must not own reusable credentials.
set -uo pipefail

ENTRYPOINT="${CREDENTIAL_ENTRYPOINT_SUT:-/usr/local/bin/entrypoint.sh}"
PHASE0_ENTRYPOINT="${CREDENTIAL_PHASE0_ENTRYPOINT_SUT:-/tmp/phase0-entrypoint.sh}"
SELFTEST="${CREDENTIAL_SELFTEST_SUT:-/usr/local/bin/selftest.sh}"
SYNC_USERS="${CREDENTIAL_SYNC_USERS_SUT:-/usr/local/bin/sync-users.sh}"
SYNC_CONFIG="${CREDENTIAL_SYNC_CONFIG_SUT:-/usr/local/bin/sync-config.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if grep -E 'kaimotest|Passw0rd!|KAIMO_TEST_(USER|PASS)' \
    "$ENTRYPOINT" "$PHASE0_ENTRYPOINT" "$SELFTEST" \
    "$SYNC_USERS" "$SYNC_CONFIG" >/dev/null; then
    echo "FAIL: an operational script retains a development credential/default."
    exit 1
fi
if grep -E 'smbclient .*-[Uu][[:space:]]' "$SELFTEST" >/dev/null \
    || ! grep -Fq -- '-A "$AUTH_FILE"' "$SELFTEST"; then
    echo "FAIL: selftest does not exclusively use an authentication file."
    exit 1
fi

unset KAIMO_SELFTEST_AUTH_FILE
if bash "$SELFTEST" >/dev/null 2>&1; then
    echo "FAIL: selftest accepted missing explicit authentication material."
    exit 1
fi

unset KAIMO_SPIKE_USER KAIMO_SPIKE_PASSWORD_FILE
if bash "$PHASE0_ENTRYPOINT" >/dev/null 2>&1; then
    echo "FAIL: Phase-0 entrypoint accepted missing explicit credentials."
    exit 1
fi

printf 'username = explicit.test\npassword = secret\n' >"$WORK/insecure-auth"
chmod 0644 "$WORK/insecure-auth"
export KAIMO_SELFTEST_AUTH_FILE="$WORK/insecure-auth"
if bash "$SELFTEST" >/dev/null 2>&1; then
    echo "FAIL: selftest accepted a group/world-readable authentication file."
    exit 1
fi

printf 'secret\n' >"$WORK/insecure-password"
chmod 0644 "$WORK/insecure-password"
export KAIMO_SPIKE_USER="explicit.test"
export KAIMO_SPIKE_PASSWORD_FILE="$WORK/insecure-password"
if bash "$PHASE0_ENTRYPOINT" >/dev/null 2>&1; then
    echo "FAIL: Phase-0 entrypoint accepted an over-permissive password file."
    exit 1
fi

echo "PASS: operational paths contain no reusable development credential."
