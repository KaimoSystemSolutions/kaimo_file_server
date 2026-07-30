#!/bin/bash
# Close every active Kaimo share connection, including all open handles.
#
# Samba 4.19 does not expose a reliable username-selective close operation.
# Security-sensitive user or control-plane revocation therefore deliberately
# disconnects every registry share. This is broader than the changed identity,
# but it gives a deterministic fail-closed boundary without restarting smbd.
set -uo pipefail

SAMBA_PATH_PREFIX="${KAIMO_SAMBA_PATH_PREFIX-/opt/samba/sbin:/opt/samba/bin}"
[ -n "$SAMBA_PATH_PREFIX" ] && export PATH="$SAMBA_PATH_PREFIX:$PATH"
reason="${1:-unspecified security-state change}"

# Initial reconciliation runs before smbd. There cannot be an active SMB handle
# to revoke in that phase, so absence of the daemon is a successful no-op.
if ! pidof smbd >/dev/null 2>&1; then
    echo "[revoke-sessions] smbd is not running; no active sessions ($reason)."
    exit 0
fi

if ! current_output="$(net conf listshares 2>/dev/null)"; then
    echo "[revoke-sessions] cannot enumerate registry shares ($reason)." >&2
    exit 1
fi

closed=0
while IFS= read -r share; do
    [ -n "$share" ] || continue
    [ "$share" != "global" ] || continue
    if ! smbcontrol smbd close-share "$share" >/dev/null 2>&1; then
        echo "[revoke-sessions] cannot close share '$share' ($reason)." >&2
        exit 1
    fi
    closed=$((closed + 1))
done < <(printf '%s\n' "$current_output" | sed '/^[[:space:]]*$/d')

echo "[revoke-sessions] closed $closed active share boundary/boundaries ($reason)."
