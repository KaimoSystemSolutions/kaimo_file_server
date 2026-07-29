#!/bin/bash
# Serializes one reconciliation component and publishes its last result.
# Usage: run-sync.sh <users|shares|config> <command> [args...]
set -uo pipefail

component="${1:-}"
[ "$#" -ge 2 ] || {
    echo "[run-sync] usage: run-sync.sh <users|shares|config> <command> [args...]" >&2
    exit 2
}
case "$component" in
    users|shares|config) ;;
    *)
        echo "[run-sync] invalid component: $component" >&2
        exit 2
        ;;
esac
shift

runtime_dir="${KAIMO_SYNC_RUNNER_RUNTIME_DIR:-/run/kaimo-sync}"
state_dir="${KAIMO_SYNC_HEALTH_DIR:-/var/lib/kaimo-sync}"
# EX_TEMPFAIL distinguishes a nonblocking lock miss from a reconciliation
# failure. The periodic cycle may safely skip the former: the active owner is
# responsible for publishing its result, and sync-health detects a hung owner
# through the maximum convergence age.
lock_busy_exit=75
umask 077

prepare_private_directory() {
    local directory="$1"
    if [ -L "$directory" ]; then
        echo "[run-sync] refusing symlink directory: $directory" >&2
        return 1
    fi
    if [ ! -e "$directory" ]; then
        mkdir -m 0700 -- "$directory" || return 1
    fi
    [ -d "$directory" ] \
        && [ "$(stat -c '%u' -- "$directory" 2>/dev/null)" = "$(id -u)" ] \
        && [ "$(stat -c '%a' -- "$directory" 2>/dev/null)" = "700" ]
}

publish_result() {
    local result="$1" timestamp tmp
    timestamp="$(date +%s)" || return 1
    tmp="$(mktemp "$state_dir/$component.$result.XXXXXX")" || return 1
    printf '%s\n' "$timestamp" >"$tmp" || { rm -f -- "$tmp"; return 1; }
    chmod 0600 "$tmp" || { rm -f -- "$tmp"; return 1; }
    mv -f -- "$tmp" "$state_dir/$component.$result" || {
        rm -f -- "$tmp"
        return 1
    }
}

prepare_private_directory "$runtime_dir" || {
    echo "[run-sync] runtime directory is not private." >&2
    exit 1
}
prepare_private_directory "$state_dir" || {
    echo "[run-sync] health directory is not private." >&2
    exit 1
}

exec 9>"$runtime_dir/$component.lock" || {
    echo "[run-sync] cannot open $component lock." >&2
    exit 1
}
if ! flock -n 9; then
    echo "[run-sync] $component reconciliation is already running." >&2
    exit "$lock_busy_exit"
fi

# Keep fd 9 (and therefore the lock) in this runner until result publication,
# but close it in the reconciliation command. Any daemon/background descendant
# started by that command then cannot retain the runner's lock after the
# command itself has completed.
"$@" 9>&-
rc=$?
# Reserve EX_TEMPFAIL for this runner's pre-execution lock miss. A reconciler
# returning the same numeric value is a real executed failure and must not be
# misclassified by sync-cycle as a harmless overlap.
if [ "$rc" -eq "$lock_busy_exit" ]; then
    rc=1
fi
if [ "$rc" -eq 0 ]; then
    if ! publish_result last-success; then
        echo "[run-sync] cannot publish $component success state." >&2
        exit 1
    fi
    rm -f -- "$state_dir/$component.last-failure"
    exit 0
fi

publish_result last-failure || \
    echo "[run-sync] cannot publish $component failure state." >&2
exit "$rc"
