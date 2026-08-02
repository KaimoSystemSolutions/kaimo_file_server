#!/bin/bash
# Integration regression for Samba's setparm/getparm name canonicalization.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="${SYNC_CONFIG_SUT:-$HERE/../sync-config.sh}"
REAL_NET="${SAMBA_NET_BIN:-/opt/samba/bin/net}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK"/{bin,private,state,cache,lock}
CONFIG="$WORK/smb.conf"
cat >"$CONFIG" <<EOF
[global]
    server role = standalone server
    private dir = $WORK/private
    state directory = $WORK/state
    cache directory = $WORK/cache
    lock directory = $WORK/lock
    registry shares = yes
    include = registry
EOF

cat >"$WORK/bin/kaimo_configsync" <<'EOF'
#!/bin/bash
printf '%s\n' '{"version":1,"config":{"min_protocol":"SMB2_02","max_protocol":"SMB3_11","require_signing":true,"require_encryption":true,"enabled":true,"enable_ws_discovery":false,"enable_audit_log":false,"log_level":"Warning"}}'
EOF

cat >"$WORK/bin/net" <<'EOF'
#!/bin/bash
exec "$SAMBA_NET_BIN" "--configfile=$SAMBA_REGISTRY_TEST_CONFIG" "$@"
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
export SAMBA_NET_BIN="$REAL_NET"
export SAMBA_REGISTRY_TEST_CONFIG="$CONFIG"
export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_LOG_CONSOLE_LEVEL_FILE="$WORK/console-log-level"
export PATH="$WORK/bin:$PATH"

first_output="$(bash "$SUT")"
canonical_value="$("$REAL_NET" "--configfile=$CONFIG" conf getparm global "server smb encrypt")"
if [ "$canonical_value" != "required" ]; then
    echo "FAIL: real Samba registry did not expose server smb encrypt = required." >&2
    exit 1
fi
if "$REAL_NET" "--configfile=$CONFIG" conf getparm global "smb encrypt" >/dev/null 2>&1; then
    echo "FAIL: real Samba registry unexpectedly exposed the noncanonical smb encrypt key." >&2
    exit 1
fi

second_output="$(bash "$SUT")"
if ! grep -q '^\[sync-config\] no change\.$' <<<"$second_output"; then
    echo "FAIL: a second real-registry reconciliation was not idempotent." >&2
    printf '%s\n' "$first_output" "$second_output" >&2
    exit 1
fi

echo "PASS: config reconciliation handles Samba's canonical registry parameter names."
