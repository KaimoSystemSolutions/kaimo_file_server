#!/bin/bash
# Validate one bounded integer reconciliation interval.
set -uo pipefail

[ "$#" -eq 4 ] || {
    echo "[validate-sync-interval] usage: <name> <value> <minimum> <maximum>" >&2
    exit 2
}
name="$1"
value="$2"
minimum="$3"
maximum="$4"

case "$minimum:$maximum" in
    *[!0-9:]*|:*|*:) exit 2 ;;
esac
if [ "$minimum" -lt 1 ] || [ "$maximum" -lt "$minimum" ]; then
    exit 2
fi

case "$value" in
    ''|*[!0-9]*) ;;
    *)
        if [ "$value" -ge "$minimum" ] && [ "$value" -le "$maximum" ]; then
            exit 0
        fi
        ;;
esac

if [ "$minimum" -eq "$maximum" ]; then
    expected="$minimum seconds"
else
    expected="$minimum-$maximum seconds"
fi
echo "[entrypoint] Invalid $name='$value' (expected $expected)." >&2
exit 1
