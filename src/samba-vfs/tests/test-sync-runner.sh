#!/bin/bash
# Regression for serialized execution and convergence health publication.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNNER="${SYNC_RUNNER_SUT:-$HERE/../run-sync.sh}"
HEALTH="${SYNC_HEALTH_SUT:-$HERE/../sync-health.sh}"
WORK="$(mktemp -d)"
descendant_pid=""
cleanup() {
    if [ -n "$descendant_pid" ]; then
        kill "$descendant_pid" >/dev/null 2>&1 || true
        wait "$descendant_pid" >/dev/null 2>&1 || true
    fi
    rm -rf "$WORK"
}
trap cleanup EXIT

export KAIMO_SYNC_RUNNER_RUNTIME_DIR="$WORK/run"
export KAIMO_SYNC_HEALTH_DIR="$WORK/state"
export KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS=180

for component in users shares config; do
    "$RUNNER" "$component" /bin/true || {
        echo "FAIL: successful $component run was rejected."
        exit 1
    }
done
"$HEALTH" || {
    echo "FAIL: fresh successful reconciliations are unhealthy."
    exit 1
}

if "$RUNNER" shares /bin/false; then
    echo "FAIL: failed reconciliation reported success."
    exit 1
fi
if "$HEALTH" >/dev/null 2>&1; then
    echo "FAIL: a failure after convergence remained healthy."
    exit 1
fi

"$RUNNER" shares /bin/true || exit 1
"$HEALTH" || {
    echo "FAIL: successful retry did not restore health."
    exit 1
}

mkdir -p "$WORK/bin"
cat >"$WORK/bin/exit-75" <<'EOF'
#!/bin/bash
exit 75
EOF
chmod +x "$WORK/bin/exit-75"
"$RUNNER" shares "$WORK/bin/exit-75" >/dev/null 2>&1
reserved_command_rc=$?
if [ "$reserved_command_rc" -eq 0 ] || [ "$reserved_command_rc" -eq 75 ] \
    || [ ! -e "$KAIMO_SYNC_HEALTH_DIR/shares.last-failure" ]; then
    echo "FAIL: command exit 75 was confused with runner lock contention."
    exit 1
fi
"$RUNNER" shares /bin/true || exit 1

old="$(( $(date +%s) - 181 ))"
printf '%s\n' "$old" >"$KAIMO_SYNC_HEALTH_DIR/config.last-success"
if "$HEALTH" >/dev/null 2>&1; then
    echo "FAIL: stale convergence remained healthy."
    exit 1
fi

(
    exec 8>"$KAIMO_SYNC_RUNNER_RUNTIME_DIR/users.lock"
    flock 8
    "$RUNNER" users /bin/true >/dev/null 2>&1
    printf '%s\n' "$?" >"$WORK/locked-exit"
)
if [ "$(cat "$WORK/locked-exit")" -ne 75 ]; then
    echo "FAIL: concurrent reconciliation did not report temporary lock contention."
    exit 1
fi
if [ -e "$KAIMO_SYNC_HEALTH_DIR/users.last-failure" ]; then
    echo "FAIL: lock contention published a synthetic reconciliation failure."
    exit 1
fi

cat >"$WORK/bin/spawn-descendant" <<'EOF'
#!/bin/bash
sleep 300 &
printf '%s\n' "$!" >"$DESCENDANT_PID_FILE"
EOF
chmod +x "$WORK/bin/spawn-descendant"
export DESCENDANT_PID_FILE="$WORK/descendant.pid"

if ! "$RUNNER" config "$WORK/bin/spawn-descendant"; then
    echo "FAIL: reconciliation command that starts a descendant was rejected."
    exit 1
fi
descendant_pid="$(cat "$DESCENDANT_PID_FILE")"
if ! kill -0 "$descendant_pid" >/dev/null 2>&1; then
    echo "FAIL: descendant process did not remain alive for inheritance test."
    exit 1
fi
if [ "$(readlink "/proc/$descendant_pid/fd/9" 2>/dev/null || true)" \
     = "$KAIMO_SYNC_RUNNER_RUNTIME_DIR/config.lock" ]; then
    echo "FAIL: reconciliation descendant inherited the config lock descriptor."
    exit 1
fi
if ! "$RUNNER" config /bin/true; then
    echo "FAIL: descendant retained the config lock after reconciliation completed."
    exit 1
fi

echo "PASS: sync runner serializes reconciliation without leaking locks to descendants."
