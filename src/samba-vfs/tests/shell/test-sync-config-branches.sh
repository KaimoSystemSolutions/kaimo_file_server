#!/bin/bash
# sync-config.sh branches beyond the basic reconciliation in
# tests/test-sync-config.sh: service disable (session teardown), audit module
# toggle, reload-config, log-level mapping, WS-Discovery management and the
# bridge/size/read-back failure paths.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
SUT="${SYNC_CONFIG_SUT:-$SCRIPTS/sync-config.sh}"

WORK="$(mktemp -d)"
cleanup() {
    /usr/bin/pkill -f "$WORK/bin/wsdd" 2>/dev/null
    /usr/bin/pkill -x wsdd 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT
fail=0

export CONFIG_STATE="$WORK/config.tsv"
export CONFIG_RESPONSE="$WORK/response"
export CALL_LOG="$WORK/calls.log"
export WSDD_STATE="$WORK/wsdd.running"
export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_LOG_CONSOLE_LEVEL_FILE="$WORK/console-log-level"
mkdir -p "$WORK/bin"

cat > "$WORK/bin/kaimo_configsync" <<'EOF'
#!/bin/bash
[ "${CONFIGSYNC_EXIT:-0}" = 0 ] || { echo "bridge down" >&2; exit "$CONFIGSYNC_EXIT"; }
cat "$CONFIG_RESPONSE"
EOF
cat > "$WORK/bin/net" <<'EOF'
#!/bin/bash
echo "net $*" >> "$CALL_LOG"
[ "${1:-}" = conf ] || exit 2
case "${2:-}" in
    listshares)
        [ "${LISTSHARES_EXIT:-0}" = 0 ] || exit "$LISTSHARES_EXIT"
        printf '%s\n' global ${SHARES:-}
        ;;
    showshare)
        awk -F '\t' 'BEGIN { print "[global]" } { print "\t" $1 " = " $2 }' "$CONFIG_STATE"
        ;;
    getparm)
        [ "${GETPARM_FAIL:-}" != "$4" ] || exit 5
        awk -F '\t' -v p="$4" '$1 == p { print $2; found=1 } END { exit !found }' "$CONFIG_STATE"
        ;;
    setparm)
        parameter="$4"
        [ "$parameter" != "smb encrypt" ] || parameter="server smb encrypt"
        awk -F '\t' -v p="$parameter" '$1 != p' "$CONFIG_STATE" > "$CONFIG_STATE.tmp"
        printf '%s\t%s\n' "$parameter" "$5" >> "$CONFIG_STATE.tmp"
        mv "$CONFIG_STATE.tmp" "$CONFIG_STATE"
        ;;
    *) exit 2 ;;
esac
EOF
cat > "$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
echo "smbcontrol $*" >> "$CALL_LOG"
exit "${SMBCONTROL_EXIT:-0}"
EOF
cat > "$WORK/bin/pidof" <<'EOF'
#!/bin/bash
[ "${SMBD_RUNNING:-0}" = 1 ]
EOF
cat > "$WORK/bin/pgrep" <<'EOF'
#!/bin/bash
[ -e "$WSDD_STATE" ]
EOF
cat > "$WORK/bin/pkill" <<'EOF'
#!/bin/bash
echo "pkill $*" >> "$CALL_LOG"
[ "${PKILL_EXIT:-0}" = 0 ] || exit "$PKILL_EXIT"
[ "${PKILL_NOOP:-0}" = 1 ] || rm -f "$WSDD_STATE"
EOF
cat > "$WORK/bin/wsdd" <<'EOF'
#!/bin/bash
touch "$WSDD_STATE"
exec sleep 30
EOF
chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

response() {
    local enabled="$1" wsdd="$2" audit="$3" level="${4:-Warning}"
    printf '{"version":1,"config":{"min_protocol":"SMB2_10","max_protocol":"SMB3_11","require_signing":false,"require_encryption":false,"enabled":%s,"enable_ws_discovery":%s,"enable_audit_log":%s,"log_level":"%s"}}\n' \
        "$enabled" "$wsdd" "$audit" "$level" > "$CONFIG_RESPONSE"
}
param() { awk -F '\t' -v p="$1" '$1 == p { print $2 }' "$CONFIG_STATE"; }
reset() {
    : > "$CONFIG_STATE"
    : > "$CALL_LOG"
    rm -f "$WSDD_STATE"
}
run_sut() {
    output="$(env "$@" bash "$SUT" 2>&1)"
    rc=$?
}
check() {
    if eval "$2"; then
        echo "  ok: $1"
    else
        echo "FAIL: $1 (rc=$rc)"
        printf '%s\n' "$output" | sed 's/^/    /'
        fail=1
    fi
}

# ------------------------------------------------------------- service disable
reset; response false false false
run_sut SMBD_RUNNING=1 SHARES="projects team"
check "disable closes every registry share but not global" \
    '[ "$rc" = 0 ] && grep -qx "smbcontrol smbd close-share projects" "$CALL_LOG" && grep -qx "smbcontrol smbd close-share team" "$CALL_LOG" && ! grep -q "close-share global" "$CALL_LOG"'
check "disable is reported" 'grep -q "forced existing sessions off all shares" <<<"$output"'

reset; run_sut SMBD_RUNNING=0 SHARES="projects"
check "disable without running smbd closes nothing" \
    '[ "$rc" = 0 ] && ! grep -q close-share "$CALL_LOG" && grep -q "DISABLED" <<<"$output"'

reset; run_sut SMBD_RUNNING=1 LISTSHARES_EXIT=3
check "unenumerable shares fail the disable" \
    '[ "$rc" != 0 ] && grep -q "cannot enumerate shares" <<<"$output"'

reset; run_sut SMBD_RUNNING=1 SHARES="projects" SMBCONTROL_EXIT=1
check "undisconnectable share fails the disable" \
    '[ "$rc" != 0 ] && grep -q "cannot disconnect share .projects." <<<"$output"'

# ------------------------------------------------------------- audit toggle
reset; response true false true
run_sut
check "audit on stacks full_audit with valid operation names" \
    '[ "$rc" = 0 ] && [ "$(param "vfs objects")" = "kaimo_bridge full_audit" ] && [ "$(param "full_audit:success")" = "connect disconnect openat close renameat unlinkat mkdirat" ] && [ "$(param "full_audit:syslog")" = no ]'
response true false false
run_sut
check "audit off restores the bare module stack" '[ "$rc" = 0 ] && [ "$(param "vfs objects")" = kaimo_bridge ]'

# ------------------------------------------------------------- reload-config
reset; response true false false
run_sut SMBD_RUNNING=1
check "changed globals trigger reload-config on running smbd" \
    '[ "$rc" = 0 ] && grep -qx "smbcontrol smbd reload-config" "$CALL_LOG" && grep -q "reload-config triggered" <<<"$output"'
: > "$CALL_LOG"
run_sut SMBD_RUNNING=1
check "unchanged globals do not reload" \
    '[ "$rc" = 0 ] && ! grep -q reload-config "$CALL_LOG" && grep -q "no change" <<<"$output"'
reset; run_sut SMBD_RUNNING=1 SMBCONTROL_EXIT=1
check "failed reload fails the cycle" '[ "$rc" != 0 ] && grep -q "reload-config failed" <<<"$output"'
reset; run_sut SMBD_RUNNING=0
check "stopped smbd reads the registry at startup" \
    '[ "$rc" = 0 ] && grep -q "registry will be read at startup" <<<"$output"'

# ------------------------------------------------------------- log levels
for case in "Debug::10:Debug" "Information::5:Information" "Warning::5:Warning" \
            "Error::5:Error" "Warning:debug:10:Debug" "Debug:ERROR:5:Error"; do
    IFS=: read -r app override samba console <<<"$case"
    reset; response true false false "$app"
    run_sut KAIMO_LOG_LEVEL="$override"
    check "log level app=$app override=${override:-none} -> samba $samba / console $console" \
        '[ "$rc" = 0 ] && [ "$(param "log level")" = "$samba" ] && [ "$(cat "$KAIMO_LOG_CONSOLE_LEVEL_FILE")" = "$console" ]'
done
reset; response true false false
run_sut KAIMO_LOG_LEVEL=verbose
check "invalid log level override is rejected" '[ "$rc" != 0 ] && grep -q "Invalid KAIMO_LOG_LEVEL" <<<"$output"'

# ------------------------------------------------------------- WS-Discovery
reset; response true true false
run_sut
check "enabled WS-Discovery starts wsdd" '[ "$rc" = 0 ] && grep -q "wsdd started" <<<"$output" && [ -e "$WSDD_STATE" ]'
run_sut
check "running wsdd is left alone" '[ "$rc" = 0 ] && ! grep -q "wsdd started" <<<"$output"'
response true false false
run_sut
check "disabled WS-Discovery stops wsdd" '[ "$rc" = 0 ] && grep -q "wsdd stopped" <<<"$output" && [ ! -e "$WSDD_STATE" ]'
touch "$WSDD_STATE"; response false true false
run_sut
check "disabled SMB service also stops wsdd" '[ "$rc" = 0 ] && grep -q "wsdd stopped" <<<"$output"'
touch "$WSDD_STATE"; response true false false
run_sut PKILL_EXIT=1
check "failed wsdd stop fails the cycle" '[ "$rc" != 0 ] && grep -q "wsdd failed to stop" <<<"$output"'
touch "$WSDD_STATE"
run_sut PKILL_NOOP=1
check "surviving wsdd fails the cycle" '[ "$rc" != 0 ] && grep -q "wsdd remains active after stop" <<<"$output"'

# A PATH with every system tool except the (real or stubbed) wsdd responder.
mkdir "$WORK/no-wsdd"
for tool in /usr/bin/* /bin/* "$WORK"/bin/*; do
    case "$(basename "$tool")" in wsdd|wsdd.py) continue ;; esac
    ln -sf "$tool" "$WORK/no-wsdd/"
done
reset; response true true false
run_sut PATH="$WORK/no-wsdd"
check "requested but missing wsdd fails the cycle" \
    '[ "$rc" != 0 ] && grep -q "wsdd requested but not installed" <<<"$output"'
response true false false
run_sut PATH="$WORK/no-wsdd"
check "missing wsdd is fine when not requested" '[ "$rc" = 0 ]'

# ------------------------------------------------------------- failure paths
reset; response true false false
run_sut CONFIGSYNC_EXIT=4
check "unreachable bridge fails before any mutation" \
    '[ "$rc" != 0 ] && grep -q "Bridge unreachable (rc=4)" <<<"$output" && [ ! -s "$CONFIG_STATE" ]'
run_sut KAIMO_CONFIG_SYNC_MAX_JSON_BYTES=10
check "oversized response is rejected" '[ "$rc" != 0 ] && grep -q "exceeds size limit" <<<"$output"'
for invalid in 0 abc; do
    run_sut KAIMO_CONFIG_SYNC_MAX_JSON_BYTES="$invalid"
    check "invalid size limit '$invalid' is rejected" \
        '[ "$rc" != 0 ] && grep -q "Invalid KAIMO_CONFIG_SYNC_MAX_JSON_BYTES" <<<"$output"'
done
reset; run_sut GETPARM_FAIL="server signing"
check "unreadable written value fails verification" \
    '[ "$rc" != 0 ] && grep -q "FAILED to read back .server signing." <<<"$output"'

if [ "$fail" -ne 0 ]; then
    exit 1
fi
echo "PASS: config reconciliation covers disable, audit, reload, log levels and WS-Discovery."
