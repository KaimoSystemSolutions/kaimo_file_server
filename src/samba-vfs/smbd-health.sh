#!/bin/bash
# Verify the supervisor-published smbd identity and local control channel.
# Health is intentionally accountless: no reusable SMB credential is needed.
set -uo pipefail

pid_file="${KAIMO_SMBD_PID_FILE:-/var/run/kaimo/smbd.pid}"
expected_executable="${KAIMO_SMBD_EXPECTED_EXECUTABLE:-/opt/samba/sbin/smbd}"
smbcontrol_command="${KAIMO_SMBCONTROL_COMMAND:-/opt/samba/bin/smbcontrol}"

if [ -L "$pid_file" ] || [ ! -f "$pid_file" ]; then
    echo "[smbd-health] supervisor PID file is unavailable or unsafe." >&2
    exit 1
fi
if [ "$(stat -c '%u' -- "$pid_file" 2>/dev/null)" != "0" ] \
    || [ "$(stat -c '%a' -- "$pid_file" 2>/dev/null)" != "600" ]; then
    echo "[smbd-health] supervisor PID file must be root-owned mode 0600." >&2
    exit 1
fi

smbd_pid="$(cat "$pid_file" 2>/dev/null)"
case "$smbd_pid" in
    ''|*[!0-9]*|0)
        echo "[smbd-health] supervisor PID file is invalid." >&2
        exit 1
        ;;
esac
if ! kill -0 "$smbd_pid" 2>/dev/null; then
    echo "[smbd-health] supervised smbd process is not alive." >&2
    exit 1
fi
if [ ! -x "$expected_executable" ]; then
    echo "[smbd-health] expected smbd executable cannot be resolved." >&2
    exit 1
fi
if [ ! -x "$smbcontrol_command" ]; then
    echo "[smbd-health] smbcontrol executable cannot be resolved." >&2
    exit 1
fi

actual_executable="$(readlink -f "/proc/$smbd_pid/exe" 2>/dev/null)"
expected_executable="$(readlink -f "$expected_executable" 2>/dev/null)"
if [ -z "$actual_executable" ] || [ "$actual_executable" != "$expected_executable" ]; then
    echo "[smbd-health] PID does not identify the expected smbd executable." >&2
    exit 1
fi

if ! "$smbcontrol_command" smbd ping >/dev/null 2>&1; then
    echo "[smbd-health] smbd did not answer its local control ping." >&2
    exit 1
fi
