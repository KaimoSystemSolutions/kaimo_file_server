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
export NET_FAIL_COMMAND=""
export NET_FAIL_PARAMETER=""
export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_STORAGE_ROOT="$WORK"
mkdir -p "$WORK/bin" "$WORK/pool01/stable" "$WORK/pool01/moved" "$WORK/pool01/removed"

printf 'stable\t%s\nmoved\t%s\nremoved\t%s\n' \
    "$WORK/pool01/stable" "$WORK/pool01/moved" "$WORK/pool01/removed" > "$SHARE_STATE"
jq -n \
    --arg stable "$WORK/pool01/stable" \
    --arg moved "$WORK/pool02/moved" \
    --arg new_share "$WORK/pool02/new-share" \
    '{version:1,shares:[
      {name:"stable",path:$stable,hidden:false},
      {name:"moved",path:$moved,hidden:false},
      {name:"new-share",path:$new_share,hidden:true}
    ]}' >"$DESIRED_SHARES"
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
        parameter="${4:-}"
        case "$parameter" in
            path) column=2 ;;
            "read only") column=3 ;;
            browseable) column=4 ;;
            "guest ok") column=5 ;;
            *) exit 2 ;;
        esac
        awk -F '\t' -v wanted="$name" -v column="$column" \
            '$1 == wanted { value=$column; if (value == "") value=(column == 4 ? "yes" : "no"); print value; found=1 } END { exit !found }' \
            "$SHARE_STATE"
        ;;
    addshare)
        [ "$NET_FAIL_COMMAND" != "addshare" ] || exit 9
        path="${4:-}"
        printf '%s\t%s\tno\tyes\tno\n' "$name" "$path" >> "$SHARE_STATE"
        ;;
    setparm)
        parameter="${4:-}"
        value="${5:-}"
        [ "$NET_FAIL_COMMAND" != "setparm" ] || [ "$NET_FAIL_PARAMETER" != "$parameter" ] || exit 9
        case "$parameter" in
            path) column=2 ;;
            "read only") column=3 ;;
            browseable) column=4 ;;
            "guest ok") column=5 ;;
            *) exit 2 ;;
        esac
        awk -F '\t' -v wanted="$name" -v column="$column" -v value="$value" \
            'BEGIN { OFS="\t" } $1 == wanted { $column=value } { print }' \
            "$SHARE_STATE" > "$SHARE_STATE.tmp"
        mv "$SHARE_STATE.tmp" "$SHARE_STATE"
        ;;
    delshare)
        [ "$NET_FAIL_COMMAND" != "delshare" ] || exit 9
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

cat > "$WORK/bin/chgrp" <<'EOF'
#!/bin/bash
# The production script runs as root with the configured numeric storage GID.
# This unit test only verifies that chgrp failures are no longer ignored.
exit "${CHGRP_EXIT_CODE:-0}"
EOF

cat > "$WORK/bin/install" <<'EOF'
#!/bin/bash
target="${@: -1}"
/bin/mkdir -p -- "$target"
/bin/chmod 2770 "$target"
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

# An empty desired state is valid and must remove every registry share. This
# also guards against `jq -e` treating a filter with no output as a failure.
printf '%s\n' '{"version":1,"shares":[]}' >"$DESIRED_SHARES"
if ! empty_output="$(bash "$SUT" 2>&1)"; then
    printf '%s\n' "$empty_output"
    echo "FAIL: empty share response was rejected."
    fail=1
elif [ -s "$SHARE_STATE" ]; then
    echo "FAIL: empty desired state left registry shares behind."
    fail=1
elif ! grep -q 'done: 0 desired shares' <<<"$empty_output"; then
    echo "FAIL: empty desired state did not report successful reconciliation."
    fail=1
fi

# Mutation failures must fail the run rather than producing a misleading
# success summary.
jq -n --arg path "$WORK/pool02/failing" \
    '{version:1,shares:[{name:"failing-share",path:$path,hidden:false}]}' \
    >"$DESIRED_SHARES"
export NET_FAIL_COMMAND=addshare
if bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: failed addshare mutation reported success."
    fail=1
fi
export NET_FAIL_COMMAND=setparm
export NET_FAIL_PARAMETER=browseable
if bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: failed setparm mutation reported success."
    fail=1
fi
unset NET_FAIL_COMMAND NET_FAIL_PARAMETER

# Invalid names, control characters, duplicate names, and storage-root escapes
# must fail before registry mutation.
cp "$SHARE_STATE" "$WORK/share-state-before-invalid"
for invalid_case in \
    '{"version":1,"shares":[{"name":"global","path":"/tmp/share","hidden":false}]}' \
    '{"version":1,"shares":[{"name":"bad/name","path":"/tmp/share","hidden":false}]}' \
    '{"version":1,"shares":[{"name":"valid","path":"/tmp/bad\npath","hidden":false}]}' \
    '{"version":1,"shares":[{"name":"Dupe","path":"/tmp/a","hidden":false},{"name":"dupe","path":"/tmp/b","hidden":false}]}' \
    '{"version":2,"shares":[]}' \
    '{"version":1,"shares":[{"name":"users","path":"/tmp/u","hidden":false,"allowed_users":["-bad"]}]}' \
    '{"version":1,"shares":[{"name":"users","path":"/tmp/u","hidden":false,"allowed_users":"alice"}]}' \
    '{"version":1,"shares":[]} {"version":1,"shares":[]}'; do
    printf '%s\n' "$invalid_case" >"$DESIRED_SHARES"
    if bash "$SUT" >/dev/null 2>&1 \
        || ! cmp -s "$WORK/share-state-before-invalid" "$SHARE_STATE"; then
        echo "FAIL: invalid structured share response mutated registry state."
        fail=1
        break
    fi
done
export KAIMO_SHARE_SYNC_MAX_JSON_BYTES=invalid
if bash "$SUT" >/dev/null 2>&1 \
    || ! cmp -s "$WORK/share-state-before-invalid" "$SHARE_STATE"; then
    echo "FAIL: invalid share JSON limit mutated registry state."
    fail=1
fi
unset KAIMO_SHARE_SYNC_MAX_JSON_BYTES
jq -n --arg path "/outside/storage/share" \
    '{version:1,shares:[{name:"outside",path:$path,hidden:false}]}' \
    >"$DESIRED_SHARES"
if bash "$SUT" >/dev/null 2>&1 \
    || ! cmp -s "$WORK/share-state-before-invalid" "$SHARE_STATE"; then
    echo "FAIL: storage-root escape mutated registry state."
    fail=1
fi

# Per-user visibility: a share with allowed_users gets a security descriptor that
# grants exactly those users (by SID); shares without the list are never touched.
export NET_FAIL_COMMAND="" NET_FAIL_PARAMETER=""   # the stub reads them under set -u
export SHARESEC_DIR="$WORK/sharesec"
export SHARESEC_LOG="$WORK/sharesec.log"
mkdir -p "$SHARESEC_DIR" "$WORK/pool01/users"
: > "$SHARESEC_LOG"
cat > "$WORK/bin/pdbedit" <<'EOF'
#!/bin/bash
printf 'Unix username:        Alice\nUser SID:             S-1-5-21-1-1000\n'
printf 'Unix username:        bob\nUser SID:             S-1-5-21-1-1001\n'
EOF
cat > "$WORK/bin/sharesec" <<'EOF'
#!/bin/bash
set -u
name="$2"
case "$3" in
    --viewsddl) cat "$SHARESEC_DIR/$name" 2>/dev/null || echo 'D:(A;;FA;;;WD)' ;;
    -S) printf '%s\n' "$4" > "$SHARESEC_DIR/$name"; echo "set $name $4" >> "$SHARESEC_LOG" ;;
    *) exit 2 ;;
esac
EOF
chmod +x "$WORK/bin/pdbedit" "$WORK/bin/sharesec"

jq -n --arg users "$WORK/pool01/users" --arg plain "$WORK/pool01/stable" \
    '{version:1,shares:[
      {name:"users",path:$users,hidden:false,allowed_users:["alice","not-synced-yet"]},
      {name:"plain",path:$plain,hidden:false}
    ]}' >"$DESIRED_SHARES"
if ! acl_output="$(bash "$SUT" 2>&1)"; then
    printf '%s\n' "$acl_output"
    echo "FAIL: restricted share was rejected."
    fail=1
elif [ "$(cat "$SHARESEC_DIR/users" 2>/dev/null)" != 'D:(A;;FA;;;S-1-5-21-1-1000)' ]; then
    echo "FAIL: restricted share did not get exactly the allowed users (case-insensitive SID lookup)."
    fail=1
elif [ -e "$SHARESEC_DIR/plain" ]; then
    echo "FAIL: unrestricted share got a security descriptor."
    fail=1
fi
# An unchanged descriptor is not rewritten.
if ! bash "$SUT" >/dev/null 2>&1 || [ "$(wc -l < "$SHARESEC_LOG")" != "1" ]; then
    echo "FAIL: unchanged share permissions were rewritten."
    fail=1
fi
# An empty allow list hides the share from everyone.
jq -n --arg users "$WORK/pool01/users" \
    '{version:1,shares:[{name:"users",path:$users,hidden:false,allowed_users:[]}]}' >"$DESIRED_SHARES"
if ! bash "$SUT" >/dev/null 2>&1 || [ "$(cat "$SHARESEC_DIR/users")" != 'D:' ]; then
    echo "FAIL: empty allow list did not deny everyone."
    fail=1
fi

if [ "$fail" = "0" ]; then
    echo "PASS: share reconciliation verifies mutations and disconnects stale sessions."
    exit 0
fi

printf '%s\n' "$output"
echo "FAILED."
exit 1
