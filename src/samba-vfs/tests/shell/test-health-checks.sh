#!/bin/bash
# Every rejection branch of the container health checks (authd-health.sh,
# smbd-health.sh, sync-health.sh). Each check must fail closed with a precise
# reason and succeed only for a fully verified live state.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
AUTHD_HEALTH="$SCRIPTS/authd-health.sh"
SMBD_HEALTH="$SCRIPTS/smbd-health.sh"
SYNC_HEALTH="$SCRIPTS/sync-health.sh"

WORK="$(mktemp -d)"
LIVE_PID=""
cleanup() {
    [ -n "$LIVE_PID" ] && kill "$LIVE_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT
fail=0

expect_fail() {
    local description="$1" message="$2"
    shift 2
    local output
    if output="$("$@" 2>&1)"; then
        echo "FAIL: $description unexpectedly succeeded"
        fail=1
    elif ! grep -Fq -- "$message" <<<"$output"; then
        echo "FAIL: $description: expected '$message', got: $output"
        fail=1
    else
        echo "  ok: $description"
    fi
}

expect_ok() {
    local description="$1"
    shift
    local output
    if ! output="$("$@" 2>&1)"; then
        echo "FAIL: $description: $output"
        fail=1
    else
        echo "  ok: $description"
    fi
}

sleep 300 &
LIVE_PID=$!
sleep 0.1 &
DEAD_PID=$!
wait "$DEAD_PID"
SLEEP_EXE="$(readlink -f "/proc/$LIVE_PID/exe")"

pid_file() {
    local path="$1" content="$2" mode="${3:-600}"
    rm -f "$path"
    printf '%s\n' "$content" > "$path"
    chmod "$mode" "$path"
}

# ------------------------------------------------------------ authd-health
socket="$WORK/authz.sock"
apid="$WORK/authd.pid"
authd() {
    env KAIMO_AUTHD_SOCK="$socket" KAIMO_AUTHD_PID_FILE="$apid" \
        KAIMO_AUTHD_EXPECTED_EXECUTABLE="${EXPECTED-$SLEEP_EXE}" bash "$AUTHD_HEALTH"
}

expect_fail "authd: missing socket" "authorization socket is unavailable" authd
python3 -c "import socket,sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])" "$socket"
expect_fail "authd: missing pid file" "PID file is unavailable or unsafe" authd
ln -s /etc/hostname "$apid"
expect_fail "authd: symlinked pid file" "PID file is unavailable or unsafe" authd
pid_file "$apid" "$LIVE_PID" 644
expect_fail "authd: group-readable pid file" "root-owned mode 0600" authd
pid_file "$apid" "$LIVE_PID"
chown nobody "$apid"
expect_fail "authd: non-root pid file" "root-owned mode 0600" authd
for invalid in "" "abc" "0" "12x"; do
    pid_file "$apid" "$invalid"
    expect_fail "authd: pid content '$invalid'" "PID file is invalid" authd
done
pid_file "$apid" "$DEAD_PID"
expect_fail "authd: dead process" "authd process is not alive" authd
pid_file "$apid" "$LIVE_PID"
EXPECTED="" expect_fail "authd: unresolvable executable" "cannot be resolved" \
    env PATH=/usr/bin:/bin KAIMO_AUTHD_SOCK="$socket" KAIMO_AUTHD_PID_FILE="$apid" \
    KAIMO_AUTHD_EXPECTED_EXECUTABLE="" bash "$AUTHD_HEALTH"
EXPECTED=/bin/true expect_fail "authd: foreign executable" "does not identify" authd
expect_ok "authd: verified live process" authd

# ------------------------------------------------------------ smbd-health
spid="$WORK/smbd.pid"
mkdir "$WORK/bin"
cat > "$WORK/bin/smbcontrol" <<'EOF'
#!/bin/bash
[ "$*" = "smbd ping" ] && exit "${PING_EXIT:-0}"
exit 2
EOF
chmod +x "$WORK/bin/smbcontrol"
smbd() {
    env KAIMO_SMBD_PID_FILE="$spid" KAIMO_SMBD_EXPECTED_EXECUTABLE="${EXPECTED-$SLEEP_EXE}" \
        KAIMO_SMBCONTROL_COMMAND="${CONTROL-$WORK/bin/smbcontrol}" PING_EXIT="${PING_EXIT:-0}" \
        bash "$SMBD_HEALTH"
}

expect_fail "smbd: missing pid file" "PID file is unavailable or unsafe" smbd
pid_file "$spid" "$LIVE_PID" 640
expect_fail "smbd: wrong pid file mode" "root-owned mode 0600" smbd
pid_file "$spid" "-1"
expect_fail "smbd: invalid pid" "PID file is invalid" smbd
pid_file "$spid" "$DEAD_PID"
expect_fail "smbd: dead process" "smbd process is not alive" smbd
pid_file "$spid" "$LIVE_PID"
EXPECTED="$WORK/missing-smbd" expect_fail "smbd: missing executable" \
    "expected smbd executable cannot be resolved" smbd
CONTROL="$WORK/missing-smbcontrol" expect_fail "smbd: missing smbcontrol" \
    "smbcontrol executable cannot be resolved" smbd
EXPECTED=/bin/true expect_fail "smbd: foreign executable" "does not identify" smbd
PING_EXIT=1 expect_fail "smbd: failed control ping" "did not answer its local control ping" smbd
expect_ok "smbd: verified live process" smbd

# ------------------------------------------------------------ sync-health
state="$WORK/sync"
mkdir "$state"
sync_health() {
    env KAIMO_SYNC_HEALTH_DIR="$state" KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS="${MAX_AGE:-180}" \
        bash "$SYNC_HEALTH"
}
now="$(date +%s)"
converge() {
    for component in users shares config; do
        printf '%s\n' "$now" > "$state/$component.last-success"
        rm -f "$state/$component.last-failure"
    done
}

for invalid in "x" "0" "-5"; do
    MAX_AGE="$invalid" expect_fail "sync: max age '$invalid'" "invalid maximum age" \
        env KAIMO_SYNC_HEALTH_DIR="$state" KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS="$invalid" \
        bash "$SYNC_HEALTH"
done
expect_fail "sync: never converged" "users has never converged" sync_health
converge
expect_ok "sync: all components current" sync_health
printf 'yesterday\n' > "$state/shares.last-success"
expect_fail "sync: invalid success state" "invalid shares success state" sync_health
converge
printf '%s\n' "$((now + 1))" > "$state/config.last-failure"
expect_fail "sync: failure after success" "config failed after its last convergence" sync_health
printf 'garbage\n' > "$state/config.last-failure"
expect_fail "sync: unreadable failure state counts as now" "config failed after" sync_health
printf '%s\n' "$((now - 100))" > "$state/config.last-failure"
expect_ok "sync: older failure is superseded" sync_health
converge
printf '%s\n' "$((now - 1000))" > "$state/users.last-success"
expect_fail "sync: stale convergence" "users convergence is stale" sync_health
printf '%s\n' "$((now + 1000))" > "$state/users.last-success"
expect_fail "sync: success timestamp in the future" "users convergence is stale" sync_health

if [ "$fail" -ne 0 ]; then
    exit 1
fi
echo "PASS: health checks fail closed for every unverifiable state."
