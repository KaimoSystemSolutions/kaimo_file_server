#!/bin/bash
# Phase 4 - Protocol settings: mirrors SMB protocol/security options
# (dialect range, signing, encryption) from Kaimo DB (ISmbConfigStore, maintained
# via web UI) into Samba's global registry config (net conf setparm global).
# Corresponds to what SmbServer.LoadProtocolSettings() fed into the old
# .NET SMB Server at (re)start.
#
# smbd adopts changed globals after `smbcontrol smbd reload-config` (only
# new connections; existing ones stay). Idempotent; reload ONLY on actual
# change and only if smbd is running (at initial sync before smbd start, smbd
# reads the registry fresh anyway at startup).
#
# Exit 0 only on successful retrieval from bridge (for retry loop in entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_configsync 2>>/tmp/configsync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-config] Bridge unreachable (rc=$rc) - see /tmp/configsync.err"
    exit 1
fi

IFS=$'\t' read -r min_proto max_proto req_sign req_enc enabled <<< "$OUT"
if [ -z "${min_proto:-}" ] || [ -z "${max_proto:-}" ]; then
    echo "[sync-config] empty/incomplete response, skipped."
    exit 0
fi

# Phase 5: on/off state. The authoritative gate is the bridge's AuthorizeConnect
# (deny-all when disabled), enforced in the VFS connect hook — smbd keeps listening
# but grants no TREE_CONNECT. Logged here for operator visibility.
if [ "${enabled:-1}" = "0" ]; then
    echo "[sync-config] smb service: DISABLED (bridge denies all TREE_CONNECT)."
else
    echo "[sync-config] smb service: enabled."
fi

# Bool -> Samba semantics.
[ "${req_sign:-0}" = "1" ] && signing="mandatory" || signing="auto"
[ "${req_enc:-0}"  = "1" ] && encrypt="required"  || encrypt="default"

changed=0
# apply <param> <value>: sets a global registry parameter only if it differs
# from current (net conf getparm gives error on unset -> curr empty).
apply() {
    local param="$1" value="$2" curr
    curr="$(net conf getparm global "$param" 2>/dev/null)"
    if [ "${curr:-}" != "$value" ]; then
        if net conf setparm global "$param" "$value" >/dev/null 2>&1; then
            echo "[sync-config] $param: '${curr:-<unset>}' -> '$value'"
            changed=1
        fi
    fi
}

apply "server min protocol" "$min_proto"
apply "server max protocol" "$max_proto"
apply "server signing"      "$signing"
apply "smb encrypt"         "$encrypt"

if [ "$changed" = "1" ]; then
    if pidof smbd >/dev/null 2>&1; then
        if smbcontrol smbd reload-config >/dev/null 2>&1; then
            echo "[sync-config] smbd reload-config triggered."
        else
            echo "[sync-config] reload-config failed (smbd not ready?)."
        fi
    else
        echo "[sync-config] smbd not yet running - registry will be read at startup."
    fi
else
    echo "[sync-config] no change."
fi
exit 0
