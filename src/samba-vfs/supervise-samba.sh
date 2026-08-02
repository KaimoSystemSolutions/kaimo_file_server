#!/bin/bash
# Supervises the two security-critical Samba runtime processes as one unit.
# If either smbd or kaimo_authd exits, the peer is terminated and PID 1 exits
# non-zero so the container cannot remain nominally alive without authorization.
set -uo pipefail

AUTHD_PID=""
SMBD_PID=""
WATCHDOG_PID=""
AUTHD_SOCKET="${KAIMO_AUTHD_SOCK:-/var/run/kaimo/authz.sock}"
AUTHD_PID_FILE="${KAIMO_AUTHD_PID_FILE:-/var/run/kaimo/authd.pid}"
SMBD_PID_FILE="${KAIMO_SMBD_PID_FILE:-/var/run/kaimo/smbd.pid}"
READY_ATTEMPTS="${KAIMO_AUTHD_READY_ATTEMPTS:-50}"
STOP_GRACE_SECONDS="${KAIMO_SUPERVISOR_STOP_GRACE_SECONDS:-5}"

validate_bounded_integer() {
    local name="$1"
    local value="$2"
    local minimum="$3"
    local maximum="$4"
    case "$value" in
        ''|*[!0-9]*)
            echo "[supervisor] Invalid $name='$value'." >&2
            return 1
            ;;
    esac
    if [ "$value" -lt "$minimum" ] || [ "$value" -gt "$maximum" ]; then
        echo "[supervisor] $name must be between $minimum and $maximum." >&2
        return 1
    fi
}

validate_bounded_integer KAIMO_AUTHD_READY_ATTEMPTS "$READY_ATTEMPTS" 1 600 || exit 1
validate_bounded_integer KAIMO_SUPERVISOR_STOP_GRACE_SECONDS \
    "$STOP_GRACE_SECONDS" 1 30 || exit 1

remove_runtime_state() {
    rm -f -- "$AUTHD_PID_FILE" "$SMBD_PID_FILE" "${KAIMO_SMBD_LOG_PIPE:-/var/run/kaimo/smbd-log.pipe}"
    [ -S "$AUTHD_SOCKET" ] && rm -f -- "$AUTHD_SOCKET"
}

terminate_children() {
    trap - TERM INT HUP QUIT
    [ -n "$AUTHD_PID" ] && kill -TERM "$AUTHD_PID" 2>/dev/null || true
    [ -n "$SMBD_PID" ] && kill -TERM "$SMBD_PID" 2>/dev/null || true
    [ -n "${LOG_FORWARDER_PID:-}" ] && kill -TERM "$LOG_FORWARDER_PID" 2>/dev/null || true

    # Bound shutdown even if a child ignores SIGTERM.
    (
        sleep "$STOP_GRACE_SECONDS"
        [ -n "$AUTHD_PID" ] && kill -KILL "$AUTHD_PID" 2>/dev/null || true
        [ -n "$SMBD_PID" ] && kill -KILL "$SMBD_PID" 2>/dev/null || true
        [ -n "${LOG_FORWARDER_PID:-}" ] && kill -KILL "$LOG_FORWARDER_PID" 2>/dev/null || true
    ) &
    WATCHDOG_PID=$!

    [ -n "$AUTHD_PID" ] && wait "$AUTHD_PID" 2>/dev/null || true
    [ -n "$SMBD_PID" ] && wait "$SMBD_PID" 2>/dev/null || true
    [ -n "${LOG_FORWARDER_PID:-}" ] && wait "$LOG_FORWARDER_PID" 2>/dev/null || true
    kill "$WATCHDOG_PID" 2>/dev/null || true
    wait "$WATCHDOG_PID" 2>/dev/null || true
    remove_runtime_state
}

handle_signal() {
    local signal_number="$1"
    echo "[supervisor] Signal $signal_number received; stopping authd and smbd."
    terminate_children
    exit $((128 + signal_number))
}

trap 'handle_signal 1' HUP
trap 'handle_signal 2' INT
trap 'handle_signal 3' QUIT
trap 'handle_signal 15' TERM

rm -f -- "$AUTHD_PID_FILE" "$SMBD_PID_FILE"
if [ -e "$AUTHD_SOCKET" ] || [ -L "$AUTHD_SOCKET" ]; then
    if [ -L "$AUTHD_SOCKET" ] || [ ! -S "$AUTHD_SOCKET" ]; then
        echo "[supervisor] Refusing unsafe pre-existing authd socket path." >&2
        exit 1
    fi
    rm -f -- "$AUTHD_SOCKET"
fi

echo "[supervisor] Starting kaimo_authd ..."
kaimo_authd &
AUTHD_PID=$!

authd_ready=0
for _ in $(seq 1 "$READY_ATTEMPTS"); do
    if ! kill -0 "$AUTHD_PID" 2>/dev/null; then
        wait "$AUTHD_PID" 2>/dev/null
        authd_status=$?
        echo "[supervisor] kaimo_authd exited before readiness (status=$authd_status)." >&2
        remove_runtime_state
        [ "$authd_status" -eq 0 ] && exit 1
        exit "$authd_status"
    fi
    if [ -S "$AUTHD_SOCKET" ]; then
        authd_ready=1
        break
    fi
    sleep 0.1
done

if [ "$authd_ready" -ne 1 ] || ! kill -0 "$AUTHD_PID" 2>/dev/null; then
    echo "[supervisor] kaimo_authd did not become ready within the bounded startup window." >&2
    terminate_children
    exit 1
fi

umask 077
publish_pid_file() {
    local pid="$1" destination="$2" description="$3" pid_tmp
    pid_tmp="${destination}.tmp.$$"
    if printf '%s\n' "$pid" >"$pid_tmp" \
        && chmod 0600 "$pid_tmp" \
        && mv -f -- "$pid_tmp" "$destination"; then
        return 0
    fi
    rm -f -- "$pid_tmp"
    echo "[supervisor] Cannot publish the $description readiness PID file." >&2
    return 1
}

if ! publish_pid_file "$AUTHD_PID" "$AUTHD_PID_FILE" authd; then
    terminate_children
    exit 1
fi

echo "[supervisor] kaimo_authd ready (pid=$AUTHD_PID); starting smbd ..."
SMBD_LOG_PIPE="${KAIMO_SMBD_LOG_PIPE:-/var/run/kaimo/smbd-log.pipe}"
LOG_FORWARDER="${KAIMO_SAMBA_LOG_FORWARDER:-/usr/local/bin/kaimo-samba-log-forwarder.py}"
rm -f -- "$SMBD_LOG_PIPE"
mkfifo -m 0600 "$SMBD_LOG_PIPE"
"$LOG_FORWARDER" <"$SMBD_LOG_PIPE" &
LOG_FORWARDER_PID=$!
smbd --foreground --no-process-group --debug-stdout >"$SMBD_LOG_PIPE" 2>&1 &
SMBD_PID=$!
if ! kill -0 "$SMBD_PID" 2>/dev/null \
    || ! publish_pid_file "$SMBD_PID" "$SMBD_PID_FILE" smbd; then
    terminate_children
    exit 1
fi

exited_pid=""
wait -n -p exited_pid "$AUTHD_PID" "$SMBD_PID" "$LOG_FORWARDER_PID"
exit_status=$?

if [ "$exited_pid" = "$AUTHD_PID" ]; then
    echo "[supervisor] kaimo_authd exited unexpectedly (status=$exit_status); stopping smbd." >&2
elif [ "$exited_pid" = "$SMBD_PID" ]; then
    echo "[supervisor] smbd exited (status=$exit_status); stopping kaimo_authd." >&2
elif [ "$exited_pid" = "$LOG_FORWARDER_PID" ]; then
    echo "[supervisor] Samba log forwarder exited (status=$exit_status); stopping the Samba unit." >&2
else
    echo "[supervisor] A supervised process exited without an identifiable PID (status=$exit_status)." >&2
fi

terminate_children

# A clean child exit is still unexpected for a long-running container.
[ "$exit_status" -eq 0 ] && exit 1
exit "$exit_status"
