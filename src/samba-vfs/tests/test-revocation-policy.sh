#!/bin/bash
# Regression for P2-13 fail-closed session revocation.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REVOKER="${SESSION_REVOKER_SUT:-$HERE/../revoke-samba-sessions.sh}"
CYCLE="${SYNC_CYCLE_SUT:-$HERE/../sync-cycle.sh}"
VALIDATOR="${SYNC_INTERVAL_VALIDATOR_SUT:-$HERE/../validate-sync-interval.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export KAIMO_SAMBA_PATH_PREFIX=""
export SMBCONTROL_LOG="$WORK/smbcontrol.log"
export RUNNER_LOG="$WORK/runner.log"
export REVOKER_LOG="$WORK/revoker.log"
mkdir -p "$WORK/bin"
: >"$SMBCONTROL_LOG"
: >"$RUNNER_LOG"
: >"$REVOKER_LOG"

cat >"$WORK/bin/pidof" <<'EOF'
#!/bin/bash
[ "${SMBD_RUNNING:-1}" = "1" ] && [ "${1:-}" = "smbd" ]
EOF
cat >"$WORK/bin/net" <<'EOF'
#!/bin/bash
[ "${1:-} ${2:-}" = "conf listshares" ] || exit 2
[ "${NET_LIST_EXIT_CODE:-0}" -eq 0 ] || exit "$NET_LIST_EXIT_CODE"
printf 'global\nprojects\narchive\n'
EOF
cat >"$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >>"$SMBCONTROL_LOG"
[ "${SMBCONTROL_EXIT_CODE:-0}" -eq 0 ] || exit "$SMBCONTROL_EXIT_CODE"
EOF
cat >"$WORK/bin/runner" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >>"$RUNNER_LOG"
exit "${RUNNER_EXIT_CODE:-0}"
EOF
cat >"$WORK/bin/revoker" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >>"$REVOKER_LOG"
exit "${REVOKER_EXIT_CODE:-0}"
EOF
chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

for valid_case in 'user 60 60 60' 'share 1 1 5' 'share 5 1 5'; do
    read -r name value minimum maximum <<<"$valid_case"
    if ! bash "$VALIDATOR" "$name" "$value" "$minimum" "$maximum" >/dev/null; then
        echo "FAIL: valid sync interval '$valid_case' was rejected."
        exit 1
    fi
done
for invalid_case in 'user 59 60 60' 'user 61 60 60' 'share 0 1 5' \
    'share 6 1 5' 'share invalid 1 5' 'share 2 0 5' 'share 2 5 1'; do
    read -r name value minimum maximum <<<"$invalid_case"
    if bash "$VALIDATOR" "$name" "$value" "$minimum" "$maximum" >/dev/null 2>&1; then
        echo "FAIL: invalid sync interval '$invalid_case' was accepted."
        exit 1
    fi
done

if ! bash "$REVOKER" "disabled account" >/dev/null \
    || ! grep -Fqx 'smbd close-share projects' "$SMBCONTROL_LOG" \
    || ! grep -Fqx 'smbd close-share archive' "$SMBCONTROL_LOG" \
    || grep -Fq 'close-share global' "$SMBCONTROL_LOG"; then
    echo "FAIL: global revocation did not close exactly the registry shares."
    exit 1
fi

export SMBD_RUNNING=0
before="$(wc -l <"$SMBCONTROL_LOG")"
if ! bash "$REVOKER" "startup" >/dev/null \
    || [ "$(wc -l <"$SMBCONTROL_LOG")" != "$before" ]; then
    echo "FAIL: pre-smbd revocation was not a successful no-op."
    exit 1
fi
unset SMBD_RUNNING

export KAIMO_SYNC_RUNNER="$WORK/bin/runner"
export KAIMO_SESSION_REVOKER="$WORK/bin/revoker"
if ! bash "$CYCLE" users /bin/true >/dev/null \
    || [ -s "$REVOKER_LOG" ]; then
    echo "FAIL: a successful sync cycle invoked fail-closed revocation."
    exit 1
fi

export RUNNER_EXIT_CODE=7
if ! bash "$CYCLE" shares /bin/false >/dev/null 2>&1 \
    || ! grep -Fqx 'shares reconciliation failure' "$REVOKER_LOG"; then
    echo "FAIL: a failed sync cycle did not contain uncertain state."
    exit 1
fi

export REVOKER_EXIT_CODE=9
if bash "$CYCLE" config /bin/false >/dev/null 2>&1; then
    echo "FAIL: unprovable session revocation did not fail the cycle."
    exit 1
fi

echo "PASS: revocation policy closes active handles or fails the Samba unit."
