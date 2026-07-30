#!/bin/bash
# Verifies the supervisor-published authd identity and its live Unix socket.
set -uo pipefail

socket_path="${KAIMO_AUTHD_SOCK:-/var/run/kaimo/authz.sock}"
pid_file="${KAIMO_AUTHD_PID_FILE:-/var/run/kaimo/authd.pid}"
expected_executable="${KAIMO_AUTHD_EXPECTED_EXECUTABLE:-$(command -v kaimo_authd 2>/dev/null)}"

if [ ! -S "$socket_path" ]; then
    echo "[authd-health] authorization socket is unavailable." >&2
    exit 1
fi
if [ -L "$pid_file" ] || [ ! -f "$pid_file" ]; then
    echo "[authd-health] supervisor PID file is unavailable or unsafe." >&2
    exit 1
fi
if [ "$(stat -c '%u' -- "$pid_file" 2>/dev/null)" != "0" ] \
    || [ "$(stat -c '%a' -- "$pid_file" 2>/dev/null)" != "600" ]; then
    echo "[authd-health] supervisor PID file must be root-owned mode 0600." >&2
    exit 1
fi

authd_pid="$(cat "$pid_file" 2>/dev/null)"
case "$authd_pid" in
    ''|*[!0-9]*|0)
        echo "[authd-health] supervisor PID file is invalid." >&2
        exit 1
        ;;
esac
if ! kill -0 "$authd_pid" 2>/dev/null; then
    echo "[authd-health] supervised authd process is not alive." >&2
    exit 1
fi
if [ -z "$expected_executable" ]; then
    echo "[authd-health] expected authd executable cannot be resolved." >&2
    exit 1
fi

actual_executable="$(readlink -f "/proc/$authd_pid/exe" 2>/dev/null)"
expected_executable="$(readlink -f "$expected_executable" 2>/dev/null)"
if [ -z "$actual_executable" ] || [ "$actual_executable" != "$expected_executable" ]; then
    echo "[authd-health] PID does not identify the expected authd executable." >&2
    exit 1
fi
