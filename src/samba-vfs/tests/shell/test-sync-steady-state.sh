#!/bin/bash
# Steady-state fast path of sync-shares.sh and sync-config.sh.
#
# Both reconcilers run every few seconds. When the bridge returns the same
# desired state and none of the local inputs a reconciliation depends on has
# changed, a cycle must not spawn the expensive Samba tools again (every `net`
# call loads the full configuration). Any change, a stale record, a corrupt
# record or a failed reconciliation must fall back to the full path.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
SHARES_SUT="${SYNC_SHARES_SUT:-$SCRIPTS/sync-shares.sh}"
CONFIG_SUT="${SYNC_CONFIG_SUT:-$SCRIPTS/sync-config.sh}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fail=0
check() {
    if eval "$2"; then
        echo "  ok: $1"
    else
        echo "FAIL: $1"
        echo "----- last output -----"
        printf '%s\n' "${out:-}"
        fail=1
    fi
}

export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_STORAGE_ROOT="$WORK"
export CALL_LOG="$WORK/calls.log"
export SHARE_STATE="$WORK/shares.tsv"
export DESIRED_SHARES="$WORK/desired.json"
export PASSDB="$WORK/passdb.tsv"
export SHARESEC_DIR="$WORK/sharesec"
export CONFIG_STATE="$WORK/config.tsv"
export CONFIG_RESPONSE="$WORK/config.json"
export KAIMO_LOG_CONSOLE_LEVEL_FILE="$WORK/console-log-level"
export KAIMO_SMB_LOCKOUT_STATE_FILE="$WORK/lockout-applied"
export NET_FAIL_COMMAND=""
mkdir -p "$WORK/bin" "$WORK/run" "$SHARESEC_DIR" "$WORK/pool01"
: >"$SHARE_STATE"
: >"$CONFIG_STATE"
printf 'alice\tS-1-5-21-1-1000\n' >"$PASSDB"

# ------------------------------------------------------------------ stubs
cat >"$WORK/bin/kaimo_sharesync" <<'EOF'
#!/bin/bash
cat "$DESIRED_SHARES"
EOF
cat >"$WORK/bin/kaimo_configsync" <<'EOF'
#!/bin/bash
cat "$CONFIG_RESPONSE"
EOF
# One stub serves both reconcilers: share sections live in $SHARE_STATE,
# [global] in $CONFIG_STATE. Every call is logged.
cat >"$WORK/bin/net" <<'EOF'
#!/bin/bash
echo "net $*" >>"$CALL_LOG"
[ "${1:-}" = conf ] || exit 2
command="${2:-}" name="${3:-}"
if [ "$name" = global ]; then
    case "$command" in
        showshare)
            awk -F '\t' 'BEGIN { print "[global]" } { print "\t" $1 " = " $2 }' "$CONFIG_STATE"
            exit ;;
        getparm)
            awk -F '\t' -v p="${4:-}" '$1 == p { print $2; f=1 } END { exit !f }' "$CONFIG_STATE"
            exit ;;
        setparm)
            parameter="${4:-}"
            [ "$parameter" != "smb encrypt" ] || parameter="server smb encrypt"
            awk -F '\t' -v p="$parameter" '$1 != p' "$CONFIG_STATE" >"$CONFIG_STATE.tmp"
            printf '%s\t%s\n' "$parameter" "${5:-}" >>"$CONFIG_STATE.tmp"
            mv "$CONFIG_STATE.tmp" "$CONFIG_STATE"
            exit ;;
        delparm) exit 0 ;;
    esac
    exit 2
fi
column_of() {
    case "$1" in
        path) echo 2 ;; "read only") echo 3 ;; browseable) echo 4 ;; "guest ok") echo 5 ;;
        *) exit 2 ;;
    esac
}
case "$command" in
    listshares) cut -f1 "$SHARE_STATE" ;;
    showshare)
        awk -F '\t' -v n="$name" '$1 == n { print "[" $1 "]"
            print "\tpath = " $2; print "\tread only = " $3
            print "\tbrowseable = " $4; print "\tguest ok = " $5; f=1 }
            END { exit !f }' "$SHARE_STATE" ;;
    getparm)
        column="$(column_of "${4:-}")" || exit 2
        awk -F '\t' -v n="$name" -v c="$column" '$1 == n { print $c; f=1 } END { exit !f }' "$SHARE_STATE" ;;
    addshare)
        [ "$NET_FAIL_COMMAND" != addshare ] || exit 9
        printf '%s\t%s\tno\tyes\tno\n' "$name" "${4:-}" >>"$SHARE_STATE" ;;
    setparm)
        column="$(column_of "${4:-}")" || exit 2
        awk -F '\t' -v n="$name" -v c="$column" -v v="${5:-}" \
            'BEGIN { OFS="\t" } $1 == n { $c=v } { print }' "$SHARE_STATE" >"$SHARE_STATE.tmp"
        mv "$SHARE_STATE.tmp" "$SHARE_STATE" ;;
    delshare)
        awk -F '\t' -v n="$name" '$1 != n' "$SHARE_STATE" >"$SHARE_STATE.tmp"
        mv "$SHARE_STATE.tmp" "$SHARE_STATE" ;;
    *) exit 2 ;;
esac
EOF
cat >"$WORK/bin/pdbedit" <<'EOF'
#!/bin/bash
echo "pdbedit $*" >>"$CALL_LOG"
if [ "${1:-}" = -P ]; then
    [ "${3:-}" = -C ] && exit 0
    echo "account policy \"$2\" value is: 0"
    exit 0
fi
while IFS=$'\t' read -r user sid; do
    printf 'Unix username:        %s\nUser SID:             %s\n' "$user" "$sid"
done <"$PASSDB"
EOF
cat >"$WORK/bin/sharesec" <<'EOF'
#!/bin/bash
echo "sharesec $*" >>"$CALL_LOG"
name="$2"
case "$3" in
    --viewsddl) cat "$SHARESEC_DIR/$name" 2>/dev/null || echo 'D:(A;;FA;;;WD)' ;;
    -S) printf '%s\n' "$4" >"$SHARESEC_DIR/$name" ;;
    *) exit 2 ;;
esac
EOF
cat >"$WORK/bin/chgrp" <<'EOF'
#!/bin/bash
echo "chgrp $*" >>"$CALL_LOG"
EOF
cat >"$WORK/bin/install" <<'EOF'
#!/bin/bash
echo "install $*" >>"$CALL_LOG"
target="${@: -1}"
/bin/mkdir -p -- "$target"
/bin/chmod 2770 "$target"
EOF
cat >"$WORK/bin/pidof" <<'EOF'
#!/bin/bash
exit 1
EOF
cat >"$WORK/bin/pgrep" <<'EOF'
#!/bin/bash
exit 1
EOF
cat >"$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
echo "smbcontrol $*" >>"$CALL_LOG"
EOF
chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

desired_shares() {
    jq -n --arg general "$WORK/pool01/general" --arg users "$WORK/pool01/users" \
        --argjson allowed "$1" \
        '{version:1,shares:[
          {name:"general",path:$general,hidden:false},
          {name:"users",path:$users,hidden:false,allowed_users:$allowed}
        ]}' >"$DESIRED_SHARES"
}
calls() { grep -c "^$1 " "$CALL_LOG" 2>/dev/null; }

# ------------------------------------------------------------------ shares
echo "== sync-shares.sh"
export KAIMO_SYNC_STATE_FILE="$WORK/run/shares.applied"
export KAIMO_SHARE_SYNC_WATCHED_FILES="$SHARE_STATE:$SHARESEC_DIR/users:$PASSDB"
unset KAIMO_SYNC_FULL_RECONCILE_SECONDS
run_shares() { : >"$CALL_LOG"; out="$(bash "$SHARES_SUT" 2>&1)"; }

desired_shares '["alice","bob"]'
run_shares; rc=$?
check "first run reconciles fully and succeeds" '[ "$rc" = 0 ] && [ "$(calls net)" -gt 0 ]'
check "first run records the applied state" '[ -s "$KAIMO_SYNC_STATE_FILE" ]'
check "users share grants the known user" \
    '[ "$(cat "$SHARESEC_DIR/users")" = "D:(A;;FA;;;S-1-5-21-1-1000)" ]'

run_shares; rc=$?
check "unchanged cycle succeeds" '[ "$rc" = 0 ]'
check "unchanged cycle spawns no Samba tool" \
    '[ "$(calls net)" = 0 ] && [ "$(calls pdbedit)" = 0 ] && [ "$(calls sharesec)" = 0 ]'
check "unchanged cycle touches no share directory" \
    '[ "$(calls chgrp)" = 0 ] && [ "$(calls install)" = 0 ]'
check "unchanged cycle says so" 'grep -q "unchanged" <<<"$out"'

# A Samba account created later (sync-users) must reach the share ACL at once.
printf 'bob\tS-1-5-21-1-1001\n' >>"$PASSDB"
run_shares
check "passdb change triggers a full run that adds the new SID" \
    '[ "$(cat "$SHARESEC_DIR/users")" = "D:(A;;FA;;;S-1-5-21-1-1000)(A;;FA;;;S-1-5-21-1-1001)" ]'

desired_shares '["alice"]'
run_shares
check "changed desired state reconciles fully" \
    '[ "$(calls net)" -gt 0 ] && [ "$(cat "$SHARESEC_DIR/users")" = "D:(A;;FA;;;S-1-5-21-1-1000)" ]'

net conf setparm general "read only" yes
run_shares
check "external registry drift is repaired" \
    '[ "$(awk -F "\t" "\$1 == \"general\" { print \$3 }" "$SHARE_STATE")" = no ]'

chmod 0700 "$WORK/pool01/general"
run_shares
check "share directory mode drift is repaired" \
    '[ "$(stat -c %a "$WORK/pool01/general")" = 2775 ]'

rm -rf "$WORK/pool01/general/.kaimo-close-captures"
run_shares
check "missing close-capture directory is recreated" \
    '[ -d "$WORK/pool01/general/.kaimo-close-captures" ] && [ "$(calls install)" -gt 0 ]'

recorded="$(tail -n +2 "$KAIMO_SYNC_STATE_FILE")"
{ echo 1; printf '%s\n' "$recorded"; } >"$KAIMO_SYNC_STATE_FILE"
run_shares
check "an expired record forces a full run" '[ "$(calls net)" -gt 0 ]'

printf 'garbage\n' >"$KAIMO_SYNC_STATE_FILE"
run_shares; rc=$?
check "a corrupt record forces a successful full run" '[ "$rc" = 0 ] && [ "$(calls net)" -gt 0 ]'

KAIMO_SYNC_FULL_RECONCILE_SECONDS=invalid run_shares
KAIMO_SYNC_FULL_RECONCILE_SECONDS=invalid run_shares
check "an invalid full-reconcile interval disables the fast path" '[ "$(calls net)" -gt 0 ]'

jq --arg p "$WORK/pool01/failing" '.shares += [{name:"failing",path:$p,hidden:false}]' \
    "$DESIRED_SHARES" >"$DESIRED_SHARES.tmp" && mv "$DESIRED_SHARES.tmp" "$DESIRED_SHARES"
NET_FAIL_COMMAND=addshare run_shares; rc=$?
check "a failed reconciliation fails" '[ "$rc" != 0 ]'
check "a failed reconciliation leaves no applied record" '[ ! -e "$KAIMO_SYNC_STATE_FILE" ]'

desired_shares '["alice"]'
unset KAIMO_SYNC_STATE_FILE
run_shares
run_shares
check "without a state file every cycle reconciles fully" '[ "$(calls net)" -gt 0 ]'

# ------------------------------------------------------------------ config
echo "== sync-config.sh"
export KAIMO_SYNC_STATE_FILE="$WORK/run/config.applied"
export KAIMO_CONFIG_SYNC_WATCHED_FILES="$CONFIG_STATE"
run_config() { : >"$CALL_LOG"; out="$(bash "$CONFIG_SUT" 2>&1)"; }
config_response() {
    printf '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":%s,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning","lockout_enabled":false,"lockout_attempts":0,"lockout_minutes":0,"hosts_allow":[]}}\n' \
        "$1" >"$CONFIG_RESPONSE"
}
signing() { awk -F '\t' '$1 == "server signing" { print $2 }' "$CONFIG_STATE"; }

config_response true
run_config; rc=$?
check "first config run reconciles fully" '[ "$rc" = 0 ] && [ "$(calls net)" -gt 0 ] && [ "$(signing)" = mandatory ]'

run_config; rc=$?
check "unchanged config cycle spawns no Samba tool" \
    '[ "$rc" = 0 ] && [ "$(calls net)" = 0 ] && [ "$(calls pdbedit)" = 0 ]'

config_response false
run_config
check "changed settings reconcile fully" '[ "$(signing)" = auto ]'

net conf setparm global "server signing" mandatory
run_config
check "external global drift is repaired" '[ "$(signing)" = auto ]'

KAIMO_LOG_LEVEL=Debug run_config
check "a changed log level override reconciles fully" \
    '[ "$(awk -F "\t" "\$1 == \"log level\" { print \$2 }" "$CONFIG_STATE")" = 10 ]'

run_config
rm -f "$KAIMO_LOG_CONSOLE_LEVEL_FILE"
run_config
check "a missing console level file is rewritten" '[ -s "$KAIMO_LOG_CONSOLE_LEVEL_FILE" ]'

printf '%s\n' '{"version":2,"config":{}}' >"$CONFIG_RESPONSE"
run_config; rc=$?
check "an invalid response still fails" '[ "$rc" != 0 ]'

if [ "$fail" = 0 ]; then
    echo "PASS: unchanged reconciliation cycles skip Samba tools; every change reconciles fully."
    exit 0
fi
exit 1
