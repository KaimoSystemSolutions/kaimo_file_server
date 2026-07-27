#!/bin/bash
# Unit test for sync-shares.sh -- runs without Samba/Docker against stub binaries.
#
# Verifies active-session handling: path changes and removals must force existing
# clients off the old service instance, while unchanged shares stay connected.
#
# Usage: bash src/samba-vfs/tests/test-sync-shares.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="${SYNC_SHARES_SUT:-$HERE/../sync-shares.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export SHARE_STATE="$WORK/shares.tsv"
export DESIRED_SHARES="$WORK/desired.tsv"
export SMBCONTROL_LOG="$WORK/smbcontrol.log"
mkdir -p "$WORK/bin" "$WORK/pool01/stable" "$WORK/pool01/moved" "$WORK/pool01/removed"

printf 'stable\t%s\nmoved\t%s\nremoved\t%s\n' \
    "$WORK/pool01/stable" "$WORK/pool01/moved" "$WORK/pool01/removed" > "$SHARE_STATE"
printf 'stable\t%s\t0\nmoved\t%s\t0\nnew-share\t%s\t1\n' \
    "$WORK/pool01/stable" "$WORK/pool02/moved" "$WORK/pool02/new-share" > "$DESIRED_SHARES"
: > "$SMBCONTROL_LOG"

cat > "$WORK/bin/kaimo_sharesync" <<'EOF'
#!/bin/bash
cat "$DESIRED_SHARES"
EOF

cat > "$WORK/bin/net" <<'EOF'
#!/bin/bash
set -u
[ "${1:-}" = "conf" ] || exit 2
command="${2:-}"
name="${3:-}"

case "$command" in
    listshares)
        cut -f1 "$SHARE_STATE"
        ;;
    showshare)
        grep -q "^${name}"$'\t' "$SHARE_STATE"
        ;;
    getparm)
        [ "${4:-}" = "path" ] || exit 2
        awk -F '\t' -v wanted="$name" '$1 == wanted { print $2; found=1 } END { exit !found }' "$SHARE_STATE"
        ;;
    addshare)
        path="${4:-}"
        printf '%s\t%s\n' "$name" "$path" >> "$SHARE_STATE"
        ;;
    setparm)
        parameter="${4:-}"
        value="${5:-}"
        if [ "$parameter" = "path" ]; then
            awk -F '\t' -v wanted="$name" -v path="$value" \
                'BEGIN { OFS="\t" } $1 == wanted { $2=path } { print }' \
                "$SHARE_STATE" > "$SHARE_STATE.tmp"
            mv "$SHARE_STATE.tmp" "$SHARE_STATE"
        fi
        ;;
    delshare)
        awk -F '\t' -v wanted="$name" '$1 != wanted' \
            "$SHARE_STATE" > "$SHARE_STATE.tmp"
        mv "$SHARE_STATE.tmp" "$SHARE_STATE"
        ;;
    *)
        exit 2
        ;;
esac
EOF

cat > "$WORK/bin/pidof" <<'EOF'
#!/bin/bash
[ "${1:-}" = "smbd" ]
EOF

cat > "$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "$SMBCONTROL_LOG"
EOF

chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

if ! output="$(bash "$SUT" 2>&1)"; then
    printf '%s\n' "$output"
    echo "FAIL: sync-shares.sh returned an error."
    exit 1
fi

fail=0
assert_closed() {
    local name="$1"
    if ! grep -qx "smbd close-share $name" "$SMBCONTROL_LOG"; then
        echo "FAIL: expected existing sessions for '$name' to be closed."
        fail=1
    fi
}

assert_closed moved
assert_closed removed

if grep -qx "smbd close-share stable" "$SMBCONTROL_LOG"; then
    echo "FAIL: unchanged share 'stable' was disconnected."
    fail=1
fi

expected_moved="$WORK/pool02/moved"
actual_moved="$(awk -F '\t' '$1 == "moved" { print $2 }' "$SHARE_STATE")"
if [ "$actual_moved" != "$expected_moved" ]; then
    echo "FAIL: moved share path was not updated."
    fail=1
fi

if grep -q '^removed'$'\t' "$SHARE_STATE"; then
    echo "FAIL: removed share remains in registry state."
    fail=1
fi

if ! grep -q '^new-share'$'\t' "$SHARE_STATE"; then
    echo "FAIL: new share was not added."
    fail=1
fi

if [ "$fail" = "0" ]; then
    echo "PASS: changed/removed shares disconnect stale sessions; unchanged shares stay connected."
    exit 0
fi

printf '%s\n' "$output"
echo "FAILED."
exit 1
