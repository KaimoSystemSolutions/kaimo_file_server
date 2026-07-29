#!/bin/bash
# Run one periodic reconciliation cycle and contain an uncertain result.
#
# A failed control-plane export or local mutation means the effective Samba
# state may be stale. Existing sessions are closed immediately. New connects
# remain protected by the VFS authorization roundtrip and fail closed when the
# bridge is unavailable. If session closure itself cannot be proven, the caller
# must terminate the supervised Samba unit.
set -uo pipefail

component="${1:-}"
[ "$#" -ge 2 ] || {
    echo "[sync-cycle] usage: sync-cycle.sh <users|shares|config> <command> [args...]" >&2
    exit 2
}
case "$component" in
    users|shares|config) ;;
    *)
        echo "[sync-cycle] invalid component: $component" >&2
        exit 2
        ;;
esac
shift

runner="${KAIMO_SYNC_RUNNER:-/usr/local/bin/run-sync.sh}"
revoker="${KAIMO_SESSION_REVOKER:-/usr/local/bin/revoke-samba-sessions.sh}"
umask 077
cycle_log="$(mktemp "${TMPDIR:-/tmp}/kaimo-sync-cycle.$component.XXXXXX")" || {
    echo "[sync-cycle] cannot create private cycle log." >&2
    exit 1
}
trap 'rm -f -- "$cycle_log"' EXIT

"$runner" "$component" "$@" >"$cycle_log" 2>&1
rc=$?
if [ "$rc" -eq 0 ]; then
    exit 0
fi

sed 's/^/[sync-cycle]   /' "$cycle_log" >&2
echo "[sync-cycle] $component reconciliation failed (rc=$rc); closing active sessions." >&2
if "$revoker" "$component reconciliation failure"; then
    # The uncertain state is contained. run-sync.sh has published the failed
    # health state; a later successful cycle restores convergence health.
    exit 0
fi

echo "[sync-cycle] cannot prove session revocation after $component failure." >&2
exit 1
