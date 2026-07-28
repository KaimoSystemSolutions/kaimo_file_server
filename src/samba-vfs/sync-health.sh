#!/bin/bash
# Fails when a reconciliation component has never converged, failed after its
# last success, or has not converged within the configured maximum age.
set -uo pipefail

state_dir="${KAIMO_SYNC_HEALTH_DIR:-/var/lib/kaimo-sync}"
max_age="${KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS:-180}"
now="$(date +%s)" || exit 1

case "$max_age" in
    ''|*[!0-9]*|0)
        echo "[sync-health] invalid maximum age: $max_age" >&2
        exit 1
        ;;
esac

healthy=1
for component in users shares config; do
    success_file="$state_dir/$component.last-success"
    failure_file="$state_dir/$component.last-failure"
    if [ ! -f "$success_file" ]; then
        echo "[sync-health] $component has never converged." >&2
        healthy=0
        continue
    fi
    success="$(cat "$success_file" 2>/dev/null)"
    case "$success" in
        ''|*[!0-9]*)
            echo "[sync-health] invalid $component success state." >&2
            healthy=0
            continue
            ;;
    esac
    if [ -f "$failure_file" ]; then
        failure="$(cat "$failure_file" 2>/dev/null)"
        case "$failure" in
            ''|*[!0-9]*) failure="$now" ;;
        esac
        if [ "$failure" -ge "$success" ]; then
            echo "[sync-health] $component failed after its last convergence." >&2
            healthy=0
            continue
        fi
    fi
    age=$((now - success))
    if [ "$age" -lt 0 ] || [ "$age" -gt "$max_age" ]; then
        echo "[sync-health] $component convergence is stale (${age}s)." >&2
        healthy=0
    fi
done

[ "$healthy" -eq 1 ]
