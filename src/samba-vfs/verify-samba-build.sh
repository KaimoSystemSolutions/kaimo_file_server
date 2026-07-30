#!/bin/bash
# Fail the image build when the installed Samba runtime, source headers, module,
# or configuration no longer match the repository's pinned compatibility
# contract. The live SMB operation gate separately proves module loading.
set -euo pipefail

PIN_FILE="${KAIMO_SAMBA_BUILD_PIN_FILE:-/usr/local/share/kaimo/samba-build.env}"
SOURCE_ROOT="${KAIMO_SAMBA_SOURCE_ROOT:-/build/samba-source}"
SMB_CONF="${KAIMO_SAMBA_TEST_CONFIG:-/opt/samba/etc/smb.conf}"

if [ ! -f "$PIN_FILE" ]; then
    echo "[samba-compat] missing pin file: $PIN_FILE" >&2
    exit 1
fi

# shellcheck disable=SC1090
. "$PIN_FILE"

case "${SAMBA_VERSION:-}" in
    ''|*[!0-9.]*)
        echo "[samba-compat] invalid SAMBA_VERSION in pin file." >&2
        exit 1
        ;;
esac
if ! printf '%s\n' "${SAMBA_TARBALL_SHA256:-}" |
    grep -Eq '^[0-9a-f]{64}$'; then
    echo "[samba-compat] tarball SHA-256 must contain 64 lowercase hex characters." >&2
    exit 1
fi
case "${SAMBA_VFS_INTERFACE_VERSION:-}" in
    ''|*[!0-9]*)
        echo "[samba-compat] invalid SAMBA_VFS_INTERFACE_VERSION in pin file." >&2
        exit 1
        ;;
esac

actual_version="$(/opt/samba/sbin/smbd --version)"
expected_version="Version ${SAMBA_VERSION}"
if [ "$actual_version" != "$expected_version" ]; then
    echo "[samba-compat] runtime version drift: '$actual_version' != '$expected_version'." >&2
    exit 1
fi

vfs_header="$SOURCE_ROOT/source3/include/vfs.h"
if [ ! -f "$vfs_header" ]; then
    echo "[samba-compat] missing VFS interface header: $vfs_header" >&2
    exit 1
fi
if ! grep -Eq \
    "^#define[[:space:]]+SMB_VFS_INTERFACE_VERSION[[:space:]]+${SAMBA_VFS_INTERFACE_VERSION}([[:space:]]|$)" \
    "$vfs_header"; then
    echo "[samba-compat] source VFS ABI does not equal ${SAMBA_VFS_INTERFACE_VERSION}." >&2
    exit 1
fi

module_path="$(find /opt/samba -type f -name '*kaimo_bridge*.so' -print -quit)"
if [ -z "$module_path" ]; then
    echo "[samba-compat] installed kaimo_bridge module was not found." >&2
    exit 1
fi
# Search the ELF directly. With `set -o pipefail`, the previous
# `strings | grep -q` pipeline could report failure after grep found the marker:
# grep exited early and strings then received SIGPIPE.
if ! LC_ALL=C grep -aFq -- "kaimo_bridge build [" "$module_path"; then
    echo "[samba-compat] installed module is not the real Kaimo VFS build." >&2
    exit 1
fi

/opt/samba/bin/testparm --suppress-prompt "$SMB_CONF" >/tmp/kaimo-testparm.out
grep -Fq "kaimo_bridge" /tmp/kaimo-testparm.out || {
    echo "[samba-compat] testparm output does not activate kaimo_bridge." >&2
    exit 1
}
rm -f /tmp/kaimo-testparm.out

printf '[samba-compat] verified Samba %s, VFS ABI %s, module %s, and testparm.\n' \
    "$SAMBA_VERSION" "$SAMBA_VFS_INTERFACE_VERSION" "$module_path"
