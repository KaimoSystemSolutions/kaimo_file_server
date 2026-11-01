#!/bin/bash
# sync-config.sh: SMB account lockout (tdbsam account policies) and the
# `hosts allow` network list (AP8b/AP8c). Runs against stateful net/pdbedit
# stubs; the real-registry behaviour is covered by
# tests/test-sync-config-samba-registry.sh.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
SUT="${SYNC_CONFIG_SUT:-$SCRIPTS/sync-config.sh}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fail=0

export CONFIG_STATE="$WORK/config.tsv"
export POLICY_STATE="$WORK/policy.tsv"
export CONFIG_RESPONSE="$WORK/response"
export CALL_LOG="$WORK/calls.log"
export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_LOG_CONSOLE_LEVEL_FILE="$WORK/console-log-level"
export KAIMO_SESSION_REVOKER="$WORK/bin/revoke-samba-sessions"
export KAIMO_SMB_LOCKOUT_STATE_FILE="$WORK/lockout-applied"
mkdir -p "$WORK/bin"

cat >"$WORK/bin/kaimo_configsync" <<'EOF'
#!/bin/bash
cat "$CONFIG_RESPONSE"
EOF
cat >"$WORK/bin/net" <<'EOF'
#!/bin/bash
echo "net $*" >>"$CALL_LOG"
[ "${1:-}" = conf ] || exit 2
case "${2:-}" in
    listshares) printf 'global\nprojects\n' ;;
    showshare) awk -F '\t' 'BEGIN { print "[global]" } { print "\t" $1 " = " $2 }' "$CONFIG_STATE" ;;
    getparm) awk -F '\t' -v p="$4" '$1 == p { print $2; found=1 } END { exit !found }' "$CONFIG_STATE" ;;
    setparm|delparm)
        [ "$2" != delparm ] || [ "${NET_FAIL_DELPARM:-0}" != 1 ] || exit 9
        parameter="$4"
        [ "$parameter" != "smb encrypt" ] || parameter="server smb encrypt"
        awk -F '\t' -v p="$parameter" '$1 != p' "$CONFIG_STATE" >"$CONFIG_STATE.tmp"
        [ "$2" = delparm ] || printf '%s\t%s\n' "$parameter" "$5" >>"$CONFIG_STATE.tmp"
        mv "$CONFIG_STATE.tmp" "$CONFIG_STATE"
        ;;
    *) exit 2 ;;
esac
EOF
# pdbedit -P <policy> [-C <value>]: prints Samba's "value is: N" line.
cat >"$WORK/bin/pdbedit" <<'EOF'
#!/bin/bash
echo "pdbedit $*" >>"$CALL_LOG"
[ "${1:-}" = -P ] || exit 2
policy="$2"
if [ "${3:-}" = -C ]; then
    [ "${PDBEDIT_FAIL_POLICY:-}" != "$policy" ] || exit 7
    # Reports success without storing the value (read-back must catch it).
    [ "${PDBEDIT_IGNORE_POLICY:-}" != "$policy" ] || exit 0
    awk -F '\t' -v p="$policy" '$1 != p' "$POLICY_STATE" >"$POLICY_STATE.tmp"
    printf '%s\t%s\n' "$policy" "$4" >>"$POLICY_STATE.tmp"
    mv "$POLICY_STATE.tmp" "$POLICY_STATE"
fi
value="$(awk -F '\t' -v p="$policy" '$1 == p { print $2 }' "$POLICY_STATE")"
echo "account policy \"$policy\" description: test" >&2
echo "account policy \"$policy\" value is: ${value:-0}"
EOF
cat >"$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
echo "smbcontrol $*" >>"$CALL_LOG"
EOF
cat >"$WORK/bin/revoke-samba-sessions" <<'EOF'
#!/bin/bash
echo "revoke $*" >>"$CALL_LOG"
exit "${REVOKE_EXIT:-0}"
EOF
cat >"$WORK/bin/pidof" <<'EOF'
#!/bin/bash
[ "${SMBD_RUNNING:-0}" = 1 ]
EOF
cat >"$WORK/bin/pgrep" <<'EOF'
#!/bin/bash
exit 1
EOF
chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

# response <lockout_enabled> <attempts> <minutes> <hosts_allow JSON array>
response() {
    printf '{"version":1,"config":{"min_protocol":"SMB2_10","max_protocol":"SMB3_11","require_signing":false,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning","lockout_enabled":%s,"lockout_attempts":%s,"lockout_minutes":%s,"hosts_allow":%s}}\n' \
        "$1" "$2" "$3" "$4" >"$CONFIG_RESPONSE"
}
param() { awk -F '\t' -v p="$1" '$1 == p { print $2; found=1 } END { exit !found }' "$CONFIG_STATE"; }
policy() { awk -F '\t' -v p="$1" '$1 == p { print $2 }' "$POLICY_STATE"; }
reset() { : >"$CONFIG_STATE"; : >"$POLICY_STATE"; : >"$CALL_LOG"; rm -f "$KAIMO_SMB_LOCKOUT_STATE_FILE"; }
run_sut() { : >"$CALL_LOG"; output="$(env "$@" bash "$SUT" 2>&1)"; rc=$?; }
check() {
    if eval "$2"; then
        echo "  ok: $1"
    else
        echo "FAIL: $1 (rc=$rc)"
        printf '%s\n' "$output" | sed 's/^/    /'
        fail=1
    fi
}

# ------------------------------------------------------------------ lockout
reset; response true 10 15 '[]'
run_sut
check "lockout on applies attempts, duration and reset window" \
    '[ "$rc" = 0 ] && [ "$(policy "bad lockout attempt")" = 10 ] && [ "$(policy "lockout duration")" = 15 ] && [ "$(policy "reset count minutes")" = 15 ]'

run_sut
check "unchanged lockout values do not call pdbedit again" \
    '[ "$rc" = 0 ] && ! grep -q "^pdbedit" "$CALL_LOG"'

response false 10 15 '[]'
run_sut
check "lockout off sets bad lockout attempt to 0" \
    '[ "$rc" = 0 ] && [ "$(policy "bad lockout attempt")" = 0 ]'

# A restarted container starts with an empty state file: values are re-applied.
rm -f "$KAIMO_SMB_LOCKOUT_STATE_FILE"; response true 3 60 '[]'
run_sut
check "missing applied-state file re-applies the policies" \
    '[ "$rc" = 0 ] && [ "$(policy "bad lockout attempt")" = 3 ] && [ "$(policy "lockout duration")" = 60 ]'

reset; response true 10 15 '[]'
run_sut PDBEDIT_FAIL_POLICY="lockout duration"
check "failed policy write fails the cycle and is retried" \
    '[ "$rc" != 0 ] && [ ! -e "$KAIMO_SMB_LOCKOUT_STATE_FILE" ]'
run_sut
check "retry after a failed policy write converges" \
    '[ "$rc" = 0 ] && [ "$(policy "lockout duration")" = 15 ]'

# An old bridge sends no lockout fields (proto3 defaults): off, nothing invalid.
reset; response false 0 0 '[]'
run_sut
check "old-bridge defaults switch lockout off" \
    '[ "$rc" = 0 ] && [ "$(policy "bad lockout attempt")" = 0 ]'

# -------------------------------------------------------------- hosts allow
reset; response true 10 15 '["10.0.0.0/8","fd00::/8"]'
run_sut SMBD_RUNNING=1
check "hosts allow always starts with loopback" \
    '[ "$rc" = 0 ] && [ "$(param "hosts allow")" = "127.0.0.1 ::1 10.0.0.0/8 fd00::/8" ]'
check "a changed hosts allow disconnects existing sessions after reload" \
    'grep -q "^smbcontrol smbd reload-config" "$CALL_LOG" && grep -q "^revoke " "$CALL_LOG"'

reset; response true 10 15 '["::10.0.0.0/104","::ffff:0:10.0.0.0/104"]'
run_sut SMBD_RUNNING=1
# .NET writes IPv4-compatible/translated IPv6 networks with a dotted tail.
check "IPv6 networks with a dotted IPv4 tail are accepted" \
    '[ "$rc" = 0 ] && [ "$(param "hosts allow")" = "127.0.0.1 ::1 ::10.0.0.0/104 ::ffff:0:10.0.0.0/104" ]'

reset; response true 10 15 '["10.0.0.0/8","fd00::/8"]'
run_sut SMBD_RUNNING=1
run_sut SMBD_RUNNING=1
check "unchanged hosts allow disconnects nobody" \
    '[ "$rc" = 0 ] && ! grep -q "^revoke " "$CALL_LOG"'

response true 10 15 '[]'
run_sut SMBD_RUNNING=1
check "empty list removes hosts allow and disconnects sessions" \
    '[ "$rc" = 0 ] && ! param "hosts allow" >/dev/null && grep -q "delparm global hosts allow" "$CALL_LOG" && grep -q "^revoke " "$CALL_LOG"'

run_sut SMBD_RUNNING=1
check "absent hosts allow stays absent without delparm" \
    '[ "$rc" = 0 ] && ! grep -q "delparm" "$CALL_LOG"'

response true 10 15 '["192.168.0.0/16"]'
run_sut SMBD_RUNNING=1 REVOKE_EXIT=5
check "failed session revocation fails the cycle" '[ "$rc" != 0 ]'

reset; response true 10 15 '["10.0.0.0/8"]'
run_sut
response true 10 15 '[]'
run_sut NET_FAIL_DELPARM=1
check "failed hosts allow removal fails the cycle" '[ "$rc" != 0 ] && param "hosts allow" >/dev/null'

# ------------------------------------------------------- lockout failure paths
reset; response true 10 15 '[]'
run_sut PDBEDIT_IGNORE_POLICY="reset count minutes"
check "unverifiable policy write fails the cycle" \
    '[ "$rc" != 0 ] && grep -q "FAILED verification for account policy" <<<"$output" && [ ! -e "$KAIMO_SMB_LOCKOUT_STATE_FILE" ]'

reset
run_sut KAIMO_SMB_LOCKOUT_STATE_FILE="$WORK/missing/lockout-applied"
check "an unrecordable applied state fails the cycle" \
    '[ "$rc" != 0 ] && grep -q "cannot record the applied lockout policy" <<<"$output"'

# ------------------------------------------------------------ schema checks
reset; response true 10 15 '["10.0.0.0/8"]'
run_sut
cp "$CONFIG_STATE" "$WORK/config-before"; cp "$POLICY_STATE" "$WORK/policy-before"
for invalid in \
    'true 10 15 ["10.0.0.0/8 127.0.0.0/8"]' \
    'true 10 15 ["10.0.0.0/8,0.0.0.0/0"]' \
    'true 10 15 ["10.0.0.1"]' \
    'true 10 15 ["300.0.0.0/8"]' \
    'true 10 15 ["10.0.0.0/33"]' \
    'true 10 15 ["fd00::/129"]' \
    'true 10 15 [7]' \
    'true 10 15 "10.0.0.0/8"' \
    'true 0 15 []' \
    'true 10 0 []' \
    'true 1001 15 []' \
    'true 10 10081 []' \
    'true -1 15 []' \
    'true "10" 15 []' \
    '"true" 10 15 []'; do
    # shellcheck disable=SC2086
    response $invalid
    run_sut
    if [ "$rc" = 0 ] || ! cmp -s "$WORK/config-before" "$CONFIG_STATE" \
        || ! cmp -s "$WORK/policy-before" "$POLICY_STATE"; then
        echo "FAIL: invalid protection settings were accepted: $invalid"
        printf '%s\n' "$output" | sed 's/^/    /'
        fail=1
    fi
done
[ "$fail" = 0 ] && echo "  ok: invalid lockout values and networks fail before any mutation"

if [ "$fail" -eq 0 ]; then
    echo "PASS: sync-config.sh applies SMB lockout and hosts allow."
    exit 0
fi
echo "FAILED."
exit 1
