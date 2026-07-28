#!/bin/bash
# Regression for serialized execution and convergence health publication.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNNER="${SYNC_RUNNER_SUT:-$HERE/../run-sync.sh}"
HEALTH="${SYNC_HEALTH_SUT:-$HERE/../sync-health.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

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
if [ "$(cat "$WORK/locked-exit")" -eq 0 ]; then
    echo "FAIL: concurrent reconciliation was not rejected."
    exit 1
fi

echo "PASS: sync runner serializes reconciliation and publishes health state."
