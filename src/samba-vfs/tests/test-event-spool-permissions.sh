#!/bin/bash
set -uo pipefail

SUT="${EVENT_SPOOL_PREPARER_SUT:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/../prepare-event-spool.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

spool="$WORK/spool"
mkdir -p "$spool/pending" "$spool/dead"
chmod 0777 "$spool" "$spool/pending" "$spool/dead"
touch "$spool/pending/existing-event"

if ! KAIMO_EVENT_SPOOL_PATH="$spool" bash "$SUT" >/dev/null; then
    echo "FAIL: insecure existing spool directories were not repaired."
    exit 1
fi
for directory in "$spool" "$spool/pending" "$spool/dead"; do
    if [ "$(stat -c '%a' -- "$directory")" != "700" ] \
        || [ "$(stat -c '%u:%g' -- "$directory")" != "$(id -u):$(id -g)" ]; then
        echo "FAIL: spool directory was not secured: $directory"
        exit 1
    fi
done
if [ ! -f "$spool/pending/existing-event" ]; then
    echo "FAIL: preparing the spool removed an existing event."
    exit 1
fi

target="$WORK/target"
mkdir "$target"
symlink_root="$WORK/symlink-root"
ln -s "$target" "$symlink_root"
if KAIMO_EVENT_SPOOL_PATH="$symlink_root" bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: symlink spool root was accepted."
    exit 1
fi

rm -rf "$spool/pending"
ln -s "$target" "$spool/pending"
if KAIMO_EVENT_SPOOL_PATH="$spool" bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: symlink spool child was accepted."
    exit 1
fi

echo "PASS: event spool permissions are repaired without following symlinks."
