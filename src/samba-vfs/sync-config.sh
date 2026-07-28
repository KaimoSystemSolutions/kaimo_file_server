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
SAMBA_PATH_PREFIX="${KAIMO_SAMBA_PATH_PREFIX-/opt/samba/sbin:/opt/samba/bin}"
[ -n "$SAMBA_PATH_PREFIX" ] && export PATH="$SAMBA_PATH_PREFIX:$PATH"

OUT="$(kaimo_configsync 2>>/tmp/configsync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-config] Bridge unreachable (rc=$rc) - see /tmp/configsync.err"
    exit 1
fi

IFS=$'\t' read -r min_proto max_proto req_sign req_enc enabled wsdd_on audit_on <<< "$OUT"
if [ -z "${min_proto:-}" ] || [ -z "${max_proto:-}" ]; then
    echo "[sync-config] empty/incomplete response." >&2
    exit 1
fi

# Phase 5: on/off state. The authoritative gate is the bridge's AuthorizeConnect
# (deny-all when disabled), enforced in the VFS connect hook — smbd keeps listening
# but grants no TREE_CONNECT. In addition (backlog #12) we force ALREADY-established
# sessions off every share when the service is disabled, so toggling SMB off is a
# hard stop for existing clients, not just a block on new TREE_CONNECTs.
if [ "${enabled:-1}" = "0" ]; then
    echo "[sync-config] smb service: DISABLED (bridge denies all TREE_CONNECT)."
    if pidof smbd >/dev/null 2>&1; then
        # Close each registry share -> disconnects clients currently using it.
        if ! share_output="$(net conf listshares 2>/dev/null)"; then
            echo "[sync-config] cannot enumerate shares for service disable." >&2
            exit 1
        fi
        while IFS= read -r _share; do
            [ -z "$_share" ] && continue
            [ "$_share" = "global" ] && continue
            smbcontrol smbd close-share "$_share" >/dev/null 2>&1 || {
                echo "[sync-config] cannot disconnect share '$_share'." >&2
                exit 1
            }
        done <<<"$share_output"
        echo "[sync-config] disabled: forced existing sessions off all shares."
    fi
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
    local param="$1" value="$2" curr actual
    curr="$(net conf getparm global "$param" 2>/dev/null)"
    if [ "${curr:-}" != "$value" ]; then
        net conf setparm global "$param" "$value" >/dev/null 2>&1 || {
            echo "[sync-config] FAILED to set '$param'." >&2
            return 1
        }
        echo "[sync-config] $param: '${curr:-<unset>}' -> '$value'"
        changed=1
    fi
    actual="$(net conf getparm global "$param" 2>/dev/null)" || {
        echo "[sync-config] FAILED to read back '$param'." >&2
        return 1
    }
    if [ "$actual" != "$value" ]; then
        echo "[sync-config] FAILED verification for '$param': '$actual' != '$value'." >&2
        return 1
    fi
}

apply "server min protocol" "$min_proto" || exit 1
apply "server max protocol" "$max_proto" || exit 1
apply "server signing"      "$signing" || exit 1
apply "smb encrypt"         "$encrypt" || exit 1

# --- Audit log (backlog #7): toggle the full_audit VFS module globally ---
# The module stack is set inline in smb.conf.vfs (`vfs objects = kaimo_bridge`);
# because `include = registry` follows, the global here overrides it. full_audit
# only LOGS, so ordering after kaimo_bridge is fine. Params are set idempotently and
# are harmless when the module isn't loaded. `full_audit:syslog = no` routes events
# through smbd's debug system (container stdout) instead of syslog.
if [ "${audit_on:-0}" = "1" ]; then
    apply "vfs objects"        "kaimo_bridge full_audit" || exit 1
    apply "full_audit:syslog"  "no" || exit 1
    apply "full_audit:priority" "NOTICE" || exit 1
    apply "full_audit:prefix"  "%u|%I|%S" || exit 1
    # IMPORTANT: these MUST be valid Samba VFS operation names, or full_audit
    # rejects the list and *fails every TREE_CONNECT* (incl. IPC$ -> no logins).
    # Samba 4.19 (ABI 49) uses the *at-based names: openat/renameat/unlinkat/mkdirat
    # (there is no legacy `open`/`rename`/`unlink`/`mkdir`/`rmdir` op). rmdir is done
    # via unlinkat(AT_REMOVEDIR), so it's covered by unlinkat.
    apply "full_audit:success" "connect disconnect openat close renameat unlinkat mkdirat" || exit 1
    apply "full_audit:failure" "connect openat renameat unlinkat mkdirat" || exit 1
else
    apply "vfs objects"        "kaimo_bridge" || exit 1
fi

if [ "$changed" = "1" ]; then
    if pidof smbd >/dev/null 2>&1; then
        if smbcontrol smbd reload-config >/dev/null 2>&1; then
            echo "[sync-config] smbd reload-config triggered."
        else
            echo "[sync-config] reload-config failed." >&2
            exit 1
        fi
    else
        echo "[sync-config] smbd not yet running - registry will be read at startup."
    fi
else
    echo "[sync-config] no change."
fi

# --- Audit self-test guard (backlog #7 safety net) ---
# full_audit fails EVERY TREE_CONNECT (incl. IPC$ -> no logins at all) if its op
# list is invalid — e.g. after a Samba upgrade renames a VFS op. The audit-log
# toggle must NEVER be able to take down the whole service. So whenever audit is
# active and smbd is up, probe a real connect (smbclient -L hits IPC$, exactly where
# full_audit would fail); if it fails, strip full_audit and reload so SMB stays
# usable. With valid op names this is a no-op.
if [ "${audit_on:-0}" = "1" ] && [ "${enabled:-1}" = "1" ] && pidof smbd >/dev/null 2>&1; then
    probe_user="${KAIMO_TEST_USER:-kaimotest}"
    probe_pass="${KAIMO_TEST_PASS:-Passw0rd!}"
    if smbclient -L localhost -U "${probe_user}%${probe_pass}" -m SMB3 >/dev/null 2>&1; then
        echo "[sync-config] audit self-test: ok."
    else
        echo "[sync-config] AUDIT SELF-TEST FAILED -> stripping full_audit to keep SMB usable."
        net conf setparm global "vfs objects" "kaimo_bridge" >/dev/null 2>&1 || \
            echo "[sync-config] emergency audit rollback failed." >&2
        smbcontrol smbd reload-config >/dev/null 2>&1 || \
            echo "[sync-config] emergency reload failed." >&2
        exit 1
    fi
fi

# --- WS-Discovery (backlog #7): manage the wsdd responder daemon ---
# There is no smb.conf parameter for WS-Discovery; it is a separate UDP responder
# (port 3702) that makes the server appear in Windows Explorer's "Network" view.
# Run it only when SMB is enabled AND the toggle is on; stop it otherwise. Idempotent.
manage_wsdd() {
    local want="$1"  # 1 = should run, 0 = should not
    local bin
    bin="$(command -v wsdd 2>/dev/null || command -v wsdd.py 2>/dev/null || true)"
    if [ -z "$bin" ]; then
        if [ "$want" = "1" ]; then
            echo "[sync-config] wsdd requested but not installed." >&2
            return 1
        fi
        return 0
    fi
    if [ "$want" = "1" ]; then
        if ! pgrep -f "$bin" >/dev/null 2>&1; then
            "$bin" >/dev/null 2>&1 &
            wsdd_pid=$!
            if ! kill -0 "$wsdd_pid" >/dev/null 2>&1; then
                echo "[sync-config] wsdd failed to start." >&2
                return 1
            fi
            echo "[sync-config] wsdd started."
        fi
    else
        if pgrep -f "$bin" >/dev/null 2>&1; then
            pkill -f "$bin" >/dev/null 2>&1 || {
                echo "[sync-config] wsdd failed to stop." >&2
                return 1
            }
            if pgrep -f "$bin" >/dev/null 2>&1; then
                echo "[sync-config] wsdd remains active after stop." >&2
                return 1
            fi
            echo "[sync-config] wsdd stopped."
        fi
    fi
}

if [ "${enabled:-1}" = "1" ] && [ "${wsdd_on:-0}" = "1" ]; then
    manage_wsdd 1 || exit 1
else
    manage_wsdd 0 || exit 1
fi

exit 0
