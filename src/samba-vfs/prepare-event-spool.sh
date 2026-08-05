#!/bin/bash
# Normalize the durable authd event spool before authd performs its independent
# fail-closed ownership and mode verification. Existing event records are kept.
set -euo pipefail

SPOOL_ROOT="${KAIMO_EVENT_SPOOL_PATH:-/var/lib/kaimo/event-spool}"
SPOOL_UID="$(id -u)"
SPOOL_GID="$(id -g)"

secure_directory() {
    local directory="$1"
    if [ -L "$directory" ]; then
        echo "[event-spool] Refusing symlink directory: $directory" >&2
        return 1
    fi
    if [ -e "$directory" ] && [ ! -d "$directory" ]; then
        echo "[event-spool] Refusing non-directory path: $directory" >&2
        return 1
    fi
    install -d -m 0700 -o "$SPOOL_UID" -g "$SPOOL_GID" -- "$directory"
}

secure_directory "$SPOOL_ROOT"
secure_directory "$SPOOL_ROOT/pending"
secure_directory "$SPOOL_ROOT/dead"

echo "[event-spool] Durable spool directories secured (mode 0700)."
