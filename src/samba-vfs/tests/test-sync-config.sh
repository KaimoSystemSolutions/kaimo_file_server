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
printf 'SMB2\tSMB3\t1\t1\t1\t0\t0\n' >"$CONFIG_RESPONSE"
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

export NET_FAIL_PARAMETER="server signing"
printf 'SMB2_10\tSMB3\t0\t0\t1\t0\t0\n' >"$CONFIG_RESPONSE"
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
: >"$CONFIG_RESPONSE"
if bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: incomplete bridge response reported success."
    exit 1
fi

echo "PASS: config reconciliation fails on unapplied or unverifiable state."
