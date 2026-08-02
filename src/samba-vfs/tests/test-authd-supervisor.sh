#!/bin/bash
# Regression tests for the authd/smbd PID-1 supervision and health contract.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUPERVISOR="${AUTHD_SUPERVISOR_SUT:-$HERE/../supervise-samba.sh}"
HEALTH="${AUTHD_HEALTH_SUT:-$HERE/../authd-health.sh}"
SMBD_HEALTH="${SMBD_HEALTH_SUT:-$HERE/../smbd-health.sh}"
WORK="$(mktemp -d)"
ACTIVE_SUPERVISOR=""

cleanup() {
    if [ -n "$ACTIVE_SUPERVISOR" ]; then
        kill -TERM "$ACTIVE_SUPERVISOR" 2>/dev/null || true
        wait "$ACTIVE_SUPERVISOR" 2>/dev/null || true
    fi
    rm -rf "$WORK"
}
trap cleanup EXIT

mkdir -p "$WORK/bin" "$WORK/run"

cat >"$WORK/bin/kaimo_authd" <<'PYEOF'
#!/usr/bin/python3
import os
import signal
import socket
import sys
import time

if os.environ.get("FAKE_AUTHD_FAIL_BEFORE_READY") == "1":
    sys.exit(23)

socket_path = os.environ["KAIMO_AUTHD_SOCK"]
try:
    os.unlink(socket_path)
except FileNotFoundError:
    pass

listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
listener.bind(socket_path)
listener.listen(1)

def stop(_signal, _frame):
    listener.close()
    sys.exit(0)

signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGINT, stop)

exit_after = os.environ.get("FAKE_AUTHD_EXIT_AFTER")
if exit_after:
    time.sleep(float(exit_after))
    sys.exit(int(os.environ.get("FAKE_AUTHD_EXIT_STATUS", "24")))

while True:
    time.sleep(1)
PYEOF

cat >"$WORK/bin/smbd" <<'SHEOF'
#!/bin/bash
printf '%s\n' "$$" >"$FAKE_SMBD_STARTED_FILE"
trap 'exit 0' TERM INT HUP QUIT
if [ -n "${FAKE_SMBD_EXIT_AFTER:-}" ]; then
    sleep "$FAKE_SMBD_EXIT_AFTER"
    exit "${FAKE_SMBD_EXIT_STATUS:-0}"
fi
while true; do sleep 1; done
SHEOF

cat >"$WORK/bin/smbcontrol" <<'SHEOF'
#!/bin/bash
[ "${1:-} ${2:-}" = "smbd ping" ] || exit 2
exit "${SMBCONTROL_EXIT_CODE:-0}"
SHEOF

chmod +x "$WORK/bin/kaimo_authd" "$WORK/bin/smbd" "$WORK/bin/smbcontrol"
export PATH="$WORK/bin:$PATH"
export KAIMO_AUTHD_SOCK="$WORK/run/authz.sock"
export KAIMO_AUTHD_PID_FILE="$WORK/run/authd.pid"
export KAIMO_SMBD_PID_FILE="$WORK/run/smbd.pid"
export KAIMO_AUTHD_READY_ATTEMPTS=20
export KAIMO_SUPERVISOR_STOP_GRACE_SECONDS=1
export KAIMO_AUTHD_EXPECTED_EXECUTABLE="$(readlink -f /usr/bin/python3)"
export KAIMO_SMBD_EXPECTED_EXECUTABLE="$(readlink -f /usr/bin/bash)"
export KAIMO_SMBCONTROL_COMMAND="$WORK/bin/smbcontrol"
export FAKE_SMBD_STARTED_FILE="$WORK/run/smbd.started"
export KAIMO_SAMBA_LOG_FORWARDER="$(command -v cat)"
export KAIMO_SMBD_LOG_PIPE="$WORK/run/smbd-log.pipe"

reset_case() {
    unset FAKE_AUTHD_FAIL_BEFORE_READY FAKE_AUTHD_EXIT_AFTER \
        FAKE_AUTHD_EXIT_STATUS FAKE_SMBD_EXIT_AFTER FAKE_SMBD_EXIT_STATUS \
        SMBCONTROL_EXIT_CODE
    rm -f "$KAIMO_AUTHD_SOCK" "$KAIMO_AUTHD_PID_FILE" \
        "$KAIMO_SMBD_PID_FILE" "$FAKE_SMBD_STARTED_FILE"
}

wait_ready() {
    for _ in $(seq 1 50); do
        [ -S "$KAIMO_AUTHD_SOCK" ] \
            && [ -s "$KAIMO_AUTHD_PID_FILE" ] \
            && [ -s "$KAIMO_SMBD_PID_FILE" ] \
            && [ -s "$FAKE_SMBD_STARTED_FILE" ] \
            && return 0
        kill -0 "$ACTIVE_SUPERVISOR" 2>/dev/null || return 1
        sleep 0.05
    done
    return 1
}

assert_dead() {
    local pid="$1"
    if kill -0 "$pid" 2>/dev/null; then
        echo "FAIL: supervised process $pid remained alive." >&2
        return 1
    fi
}

# 1) A live, correctly identified authd and socket pass health. Killing authd
# must fail the supervisor and terminate smbd.
reset_case
"$SUPERVISOR" >"$WORK/authd-exit.log" 2>&1 &
ACTIVE_SUPERVISOR=$!
wait_ready || {
    echo "FAIL: supervisor did not reach ready state." >&2
    cat "$WORK/authd-exit.log" >&2
    exit 1
}
"$HEALTH" || {
    echo "FAIL: healthy supervised authd was rejected." >&2
    exit 1
}
"$SMBD_HEALTH" || {
    echo "FAIL: healthy supervised smbd was rejected." >&2
    exit 1
}
export SMBCONTROL_EXIT_CODE=9
if "$SMBD_HEALTH" >/dev/null 2>&1; then
    echo "FAIL: smbd health ignored a failed local control ping." >&2
    exit 1
fi
unset SMBCONTROL_EXIT_CODE
authd_pid="$(cat "$KAIMO_AUTHD_PID_FILE")"
smbd_pid="$(cat "$KAIMO_SMBD_PID_FILE")"
kill -TERM "$authd_pid"
wait "$ACTIVE_SUPERVISOR"
supervisor_status=$?
ACTIVE_SUPERVISOR=""
if [ "$supervisor_status" -eq 0 ]; then
    echo "FAIL: authd exit did not fail the supervisor." >&2
    exit 1
fi
assert_dead "$smbd_pid" || exit 1
if "$HEALTH" >/dev/null 2>&1; then
    echo "FAIL: health remained successful after authd exit." >&2
    exit 1
fi
if "$SMBD_HEALTH" >/dev/null 2>&1; then
    echo "FAIL: smbd health remained successful after supervised shutdown." >&2
    exit 1
fi
echo "  ok: authd exit fails the unit, stops smbd, and removes readiness"

# 2) authd failure before socket readiness must prevent smbd startup and retain
# the child failure status.
reset_case
export FAKE_AUTHD_FAIL_BEFORE_READY=1
"$SUPERVISOR" >"$WORK/startup-failure.log" 2>&1
supervisor_status=$?
if [ "$supervisor_status" -ne 23 ] || [ -e "$FAKE_SMBD_STARTED_FILE" ]; then
    echo "FAIL: pre-readiness authd failure was not propagated." >&2
    cat "$WORK/startup-failure.log" >&2
    exit 1
fi
echo "  ok: pre-readiness authd failure prevents smbd startup"

# 3) Even a clean smbd exit is abnormal for the long-running unit and must stop
# authd, remove readiness, and return non-zero.
reset_case
export FAKE_SMBD_EXIT_AFTER=0.2
export FAKE_SMBD_EXIT_STATUS=0
"$SUPERVISOR" >"$WORK/smbd-exit.log" 2>&1
supervisor_status=$?
authd_pid="$(grep -o 'pid=[0-9]*' "$WORK/smbd-exit.log" | head -n1 | cut -d= -f2)"
if [ "$supervisor_status" -eq 0 ] || [ -z "$authd_pid" ]; then
    echo "FAIL: clean smbd exit did not fail the supervised unit." >&2
    cat "$WORK/smbd-exit.log" >&2
    exit 1
fi
assert_dead "$authd_pid" || exit 1
[ ! -e "$KAIMO_AUTHD_PID_FILE" ] || {
    echo "FAIL: smbd exit left authd readiness state behind." >&2
    exit 1
}
[ ! -e "$KAIMO_SMBD_PID_FILE" ] || {
    echo "FAIL: smbd exit left smbd readiness state behind." >&2
    exit 1
}
echo "  ok: smbd exit stops authd and fails the long-running unit"

# 4) Container shutdown signals are forwarded to both children, with bounded
# cleanup and the conventional 128+signal exit status.
reset_case
"$SUPERVISOR" >"$WORK/signal.log" 2>&1 &
ACTIVE_SUPERVISOR=$!
wait_ready || {
    echo "FAIL: signal test did not reach ready state." >&2
    exit 1
}
authd_pid="$(cat "$KAIMO_AUTHD_PID_FILE")"
smbd_pid="$(cat "$KAIMO_SMBD_PID_FILE")"
kill -TERM "$ACTIVE_SUPERVISOR"
wait "$ACTIVE_SUPERVISOR"
supervisor_status=$?
ACTIVE_SUPERVISOR=""
if [ "$supervisor_status" -ne 143 ]; then
    echo "FAIL: SIGTERM produced status $supervisor_status instead of 143." >&2
    cat "$WORK/signal.log" >&2
    exit 1
fi
assert_dead "$authd_pid" || exit 1
assert_dead "$smbd_pid" || exit 1
echo "  ok: SIGTERM is forwarded and both children are reaped"

echo "PASS: authd and smbd are supervised as one fail-fast container unit."
