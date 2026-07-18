#!/bin/bash
# Phase 4 - Share provisioning: mirrors enabled Kaimo shares live into
# Samba registry (net conf). smbd reads the registry without restart -> shares
# appear/disappear immediately. Replaces the FileSystemWatcher/SyncFromDb
# mechanism from src/Kaimo_File_Server.Smb/SmbServer.cs. Idempotent.
#
# Visibility (ABE): ONLY the hidden flag. IsShareHidden -> browseable = no
# (the share remains directly reachable via \\host\share). Hard access control
# is still decided by the VFS connect hook based on actual Kaimo ACLs.
#
# Exit 0 only on successful retrieval from bridge (for retry loop in entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_sharesync 2>>/tmp/sharesync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-shares] Bridge unreachable (rc=$rc) - see /tmp/sharesync.err"
    exit 1
fi

# --- Read desired state from bridge (name -> path / hidden) ---
# With empty `=()`-initializer, so maps are "set" even WITHOUT elements
# -> references like ${want_path[k]+x} / "${!want_path[@]}" would otherwise
# break under `set -u` with "unbound variable" (case: no/all shares disabled).
declare -A want_path=()
declare -A want_hidden=()
while IFS=$'\t' read -r name path hidden; do
    [ -z "${name:-}" ] && continue
    want_path["$name"]="$path"
    want_hidden["$name"]="${hidden:-0}"
done <<< "$OUT"

# --- Current state: shares currently in registry (one per line) ---
mapfile -t current < <(net conf listshares 2>/dev/null | sed '/^[[:space:]]*$/d')

# 1) Remove unwanted registry shares. The registry is managed
#    exclusively by this sync -> anything not in desired state is a deleted/disabled
#    Kaimo share. Never touch 'global'.
# (Element count ${#arr[@]} is safely 0 for empty array; key/value
#  expansion "${arr[@]}" of an empty array declared with `declare -A`
#  would break under `set -u` with "unbound variable" -> guard loops accordingly.)
removed=0
if (( ${#current[@]} > 0 )); then
    for name in "${current[@]}"; do
        [ -z "${name:-}" ] && continue
        [ "$name" = "global" ] && continue
        if [ -z "${want_path[$name]+x}" ]; then
            if net conf delshare "$name" 2>/dev/null; then
                echo "[sync-shares] removed: $name"
                removed=$((removed + 1))
            fi
        fi
    done
fi

# 2) Create desired shares or bring to desired state (idempotent).
added=0; updated=0
if (( ${#want_path[@]} > 0 )); then
    for name in "${!want_path[@]}"; do
        [ -z "${name:-}" ] && continue
        path="${want_path[$name]}"
        hidden="${want_hidden[$name]}"
        [ "$hidden" = "1" ] && browseable="no" || browseable="yes"

        # Samba validates on addshare that the target directory exists.
        mkdir -p "$path"
        # Give new share directory to shared storage group + setgid + g+w,
        # so SMB users (group kaimo) and Web (uid $KAIMO_STORAGE_GID) can write
        # and new files inherit the group. Complements create/directory masks
        # in smb.conf.vfs. Idempotent (enforced at each sync).
        chgrp "${KAIMO_STORAGE_GID:-1654}" "$path" 2>/dev/null || true
        chmod 2775 "$path" 2>/dev/null || true

        # Create, if not already present ...
        if net conf showshare "$name" >/dev/null 2>&1; then
            updated=$((updated + 1))
        else
            net conf addshare "$name" "$path" writeable=y guest_ok=n "Kaimo Share" >/dev/null 2>&1
            echo "[sync-shares] created: $name -> $path (browseable=$browseable)"
            added=$((added + 1))
        fi

        # ... and in ANY case (new or existing) enforce desired state with
        # CANONICAL Samba parameters. Important: `read only = no` instead of
        # the synonym `writeable` — Samba's default is `read only = yes`, otherwise
        # shares are read-only (reading via SMB works, writing fails at
        # Samba level, before ACL). Hard access control stays at the
        # VFS connect/create_file hook; here only the share base disposition.
        net conf setparm "$name" path         "$path"       >/dev/null 2>&1
        net conf setparm "$name" "read only"  no            >/dev/null 2>&1
        net conf setparm "$name" browseable   "$browseable" >/dev/null 2>&1
        net conf setparm "$name" "guest ok"   no            >/dev/null 2>&1
    done
fi

echo "[sync-shares] done: ${#want_path[@]} desired shares (${added} new, ${updated} updated, ${removed} removed)."
exit 0
