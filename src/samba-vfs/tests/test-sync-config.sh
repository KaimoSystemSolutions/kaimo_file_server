#!/bin/bash
# Regression for mutation failures and read-after-write verification.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="${SYNC_CONFIG_SUT:-$HERE/../sync-config.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export CONFIG_STATE="$WORK/config.tsv"
export CONFIG_RESPONSE="$WORK/response"
export NET_FAIL_PARAMETER=""
export NET_IGNORE_PARAMETER=""
export KAIMO_SAMBA_PATH_PREFIX=""
mkdir -p "$WORK/bin"
printf '%s\n' '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":true,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}' >"$CONFIG_RESPONSE"
: >"$CONFIG_STATE"

cat >"$WORK/bin/kaimo_configsync" <<'EOF'
#!/bin/bash
cat "$CONFIG_RESPONSE"
EOF

cat >"$WORK/bin/net" <<'EOF'
#!/bin/bash
[ "${1:-}" = "conf" ] || exit 2
command="${2:-}"
[ "$command" != "listshares" ] || exit 0
[ "$command" = "getparm" ] || [ "$command" = "setparm" ] || exit 2
[ "${3:-}" = "global" ] || exit 2
parameter="${4:-}"
if [ "$command" = "getparm" ]; then
    awk -F '\t' -v parameter="$parameter" \
        '$1 == parameter { print $2; found=1 } END { exit !found }' "$CONFIG_STATE"
    exit
fi
value="${5:-}"
[ "$NET_FAIL_PARAMETER" != "$parameter" ] || exit 9
[ "$NET_IGNORE_PARAMETER" != "$parameter" ] || exit 0
# Match Samba 4.19 registry canonicalization: setparm accepts "smb encrypt",
# while list/get expose the persisted key as "server smb encrypt".
[ "$parameter" != "smb encrypt" ] || parameter="server smb encrypt"
awk -F '\t' -v parameter="$parameter" '$1 != parameter' "$CONFIG_STATE" >"$CONFIG_STATE.tmp"
printf '%s\t%s\n' "$parameter" "$value" >>"$CONFIG_STATE.tmp"
mv "$CONFIG_STATE.tmp" "$CONFIG_STATE"
EOF

cat >"$WORK/bin/pidof" <<'EOF'
#!/bin/bash
exit 1
EOF
cat >"$WORK/bin/pgrep" <<'EOF'
#!/bin/bash
exit 1
EOF
cat >"$WORK/bin/wsdd" <<'EOF'
#!/bin/bash
exit 0
EOF

chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

if ! bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: valid configuration did not converge."
    exit 1
fi
if ! grep -q $'^log level\t1$' "$CONFIG_STATE"; then
    echo "FAIL: Settings Warning level was not mapped to Samba log level 1."
    exit 1
fi
if ! grep -q $'^server smb encrypt\trequired$' "$CONFIG_STATE" \
    || grep -q $'^smb encrypt\t' "$CONFIG_STATE"; then
    echo "FAIL: smb encrypt did not converge through Samba's canonical registry name."
    exit 1
fi

export KAIMO_LOG_LEVEL=Debug
if ! bash "$SUT" >/dev/null 2>&1 \
    || ! grep -q $'^log level\t10$' "$CONFIG_STATE"; then
    echo "FAIL: KAIMO_LOG_LEVEL did not override the Settings log level."
    exit 1
fi
unset KAIMO_LOG_LEVEL

printf '%s\n' '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":true,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Information"}}' >"$CONFIG_RESPONSE"
if ! bash "$SUT" >/dev/null 2>&1 \
    || ! grep -q $'^log level\t5$' "$CONFIG_STATE"; then
    echo "FAIL: Settings Information level was not mapped to Samba DBGLVL_INFO (5)."
    exit 1
fi

export NET_FAIL_PARAMETER="server signing"
printf '%s\n' '{"version":1,"config":{"min_protocol":"SMB2_10","max_protocol":"SMB3_11","require_signing":false,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}' >"$CONFIG_RESPONSE"
if bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: failed setparm mutation reported success."
    exit 1
fi

unset NET_FAIL_PARAMETER
export NET_IGNORE_PARAMETER="server signing"
if bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: unapplied setparm mutation passed read-back verification."
    exit 1
fi

unset NET_IGNORE_PARAMETER
cp "$CONFIG_STATE" "$WORK/config-before-invalid"
for invalid_case in \
    '' \
    '{"version":2,"config":{}}' \
    '{"version":1,"config":{"min_protocol":"NT1","max_protocol":"SMB3_11","require_signing":true,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}' \
    '{"version":1,"config":{"min_protocol":"SMB3_11","max_protocol":"SMB2_02","require_signing":true,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}' \
    '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":"true","require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}' \
    '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning","unexpected":true}}' \
    '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}} {"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":false,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}'; do
    printf '%s\n' "$invalid_case" >"$CONFIG_RESPONSE"
    if bash "$SUT" >/dev/null 2>&1 \
        || ! cmp -s "$WORK/config-before-invalid" "$CONFIG_STATE"; then
        echo "FAIL: invalid structured config response mutated registry state."
        exit 1
    fi
done
export KAIMO_CONFIG_SYNC_MAX_JSON_BYTES=invalid
if bash "$SUT" >/dev/null 2>&1 \
    || ! cmp -s "$WORK/config-before-invalid" "$CONFIG_STATE"; then
    echo "FAIL: invalid config JSON limit mutated registry state."
    exit 1
fi
unset KAIMO_CONFIG_SYNC_MAX_JSON_BYTES

echo "PASS: config reconciliation fails on unapplied or unverifiable state."
