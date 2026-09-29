#!/bin/bash
# Phase 4 - Share provisioning: mirrors enabled Kaimo shares live into
# Samba registry (net conf). smbd reads the registry without restart -> shares
# appear/disappear immediately. Replaces the FileSystemWatcher/SyncFromDb
# mechanism from src/Kaimo_File_Server.Smb/SmbServer.cs. Idempotent.
#
# Visibility (ABE): the hidden flag for every share (IsShareHidden -> browseable
# = no; the share remains directly reachable via \\host\share). A share that
# carries an `allowed_users` list (only the home-folder share "users") also gets a
# share security descriptor (sharesec) granting exactly those users, so
# `access based share enum` hides it from everyone else. Shares without a list
# keep Samba's default descriptor. Hard access control is still decided by the
# VFS connect hook based on actual Kaimo ACLs.
#
# Exit 0 only on successful retrieval from bridge (for retry loop in entrypoint).
set -uo pipefail
SAMBA_PATH_PREFIX="${KAIMO_SAMBA_PATH_PREFIX-/opt/samba/sbin:/opt/samba/bin}"
[ -n "$SAMBA_PATH_PREFIX" ] && export PATH="$SAMBA_PATH_PREFIX:$PATH"
CACHE_ROOT="$(readlink -m "${KAIMO_SNAPSHOT_CACHE_ROOT:-/data/kaimo-system/.kaimo-snapshots}")"
STORAGE_ROOT="$(readlink -m "${KAIMO_STORAGE_ROOT:-${KAIMO_STORAGE:-/data/storage}}")"

OUT="$(kaimo_sharesync 2>>/tmp/sharesync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-shares] Bridge unreachable (rc=$rc) - see /tmp/sharesync.err"
    exit 1
fi
command -v jq >/dev/null 2>&1 || {
    echo "[sync-shares] jq is required for structured synchronization." >&2
    exit 1
}
MAX_JSON_BYTES="${KAIMO_SHARE_SYNC_MAX_JSON_BYTES:-16777216}"
case "$MAX_JSON_BYTES" in
    ''|*[!0-9]*|0)
        echo "[sync-shares] Invalid KAIMO_SHARE_SYNC_MAX_JSON_BYTES." >&2
        exit 1
        ;;
esac
if [ "${#OUT}" -gt "$MAX_JSON_BYTES" ]; then
    echo "[sync-shares] Structured response exceeds size limit." >&2
    exit 1
fi
if ! SHARE_RECORDS="$(printf '%s' "$OUT" | jq -s -e -r '
    def exact_keys($expected): (keys | sort) == ($expected | sort);
    def reserved_share:
        (. | ascii_downcase) as $name
        | ($name == "global" or $name == "homes" or $name == "printers"
           or $name == "print$" or $name == "ipc$");
    if length != 1 then error("expected exactly one JSON document")
    else .[0]
    end
    | if type != "object"
       or (exact_keys(["version", "shares"]) | not)
       or .version != 1
       or (.shares | type) != "array"
       or (.shares | length) > 100000
       or (all(.shares[]; . as $share
            | ($share | type) == "object"
            and (($share | exact_keys(["name", "path", "hidden"]))
                 or ($share | exact_keys(["name", "path", "hidden", "allowed_users"])))
            and (($share.name | type) == "string")
            and ($share.name | test("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$"))
            and ($share.name | endswith(".") | not)
            and ($share.name | reserved_share | not)
            and (($share.path | type) == "string")
            and (($share.path | length) > 0 and ($share.path | length) <= 4096)
            and ($share.path | startswith("/"))
            and ($share.path | explode | all(.[]; . >= 32 and . != 127))
            and (($share.hidden | type) == "boolean")
            and (($share | has("allowed_users") | not)
                 or ((($share.allowed_users | type) == "array")
                     and ($share.allowed_users | length) <= 100000
                     and all($share.allowed_users[]; (type == "string")
                         and test("^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$"))))) | not)
       or (([.shares[].name | ascii_downcase] | length)
           != ([.shares[].name | ascii_downcase] | unique | length))
    then error("invalid share sync schema")
    # Always emit one JSON result so `jq -e` also succeeds for the valid
    # empty desired state. `join("\n")` becomes an empty string for zero
    # shares and a newline-separated TSV document otherwise.
    else ([.shares[]
           | [.name, .path, (if .hidden then "1" else "0" end),
              # "*" = unrestricted; "+" followed by a comma list = only these users.
              (if has("allowed_users") then "+" + (.allowed_users | join(",")) else "*" end)]
           | @tsv]
          | join("\n"))
    end
')"; then
    echo "[sync-shares] Invalid structured share response." >&2
    exit 1
fi

# --- Read desired state from bridge (name -> path / hidden) ---
# With empty `=()`-initializer, so maps are "set" even WITHOUT elements
# -> references like ${want_path[k]+x} / "${!want_path[@]}" would otherwise
# break under `set -u` with "unbound variable" (case: no/all shares disabled).
declare -A want_path=()
declare -A want_hidden=()
declare -A want_access=()
canonical_paths=()
while IFS=$'\t' read -r name path hidden access; do
    [ -z "${name:-}" ] && continue
    canonical_path="$(readlink -m -- "$path")"
    if [ "$canonical_path" = "$STORAGE_ROOT" ] ||
       [[ "$canonical_path/" != "$STORAGE_ROOT/"* ]]; then
        echo "[sync-shares] REJECTED path outside storage root: $name -> $canonical_path" >&2
        exit 1
    fi
    # The snapshot cache must never be published as a share, nor may a broad
    # share contain it. Excluding an unsafe definition from desired state also
    # removes a previously published registry share during the reconciliation.
    if [ "$canonical_path" = "$CACHE_ROOT" ] ||
       [[ "$canonical_path/" == "$CACHE_ROOT/"* ]] ||
       [[ "$CACHE_ROOT/" == "$canonical_path/"* ]]; then
        echo "[sync-shares] REJECTED unsafe share/cache overlap: $name -> $canonical_path" >&2
        exit 1
    fi
    want_path["$name"]="$path"
    want_hidden["$name"]="${hidden:-0}"
    want_access["$name"]="${access:-*}"
    canonical_paths+=("$canonical_path")
done <<< "$SHARE_RECORDS"
if (( ${#canonical_paths[@]} > 0 )); then
    duplicate_canonical_paths="$(
        printf '%s\n' "${canonical_paths[@]}" | sort | uniq -d
    )"
    if [ -n "$duplicate_canonical_paths" ]; then
        echo "[sync-shares] REJECTED duplicate canonical share path." >&2
        exit 1
    fi
fi

# --- Per-user share visibility (security descriptor) ---
# Samba username -> SID, loaded once per run and only when a restricted share exists.
declare -A user_sid=()
user_sids_loaded=0
load_user_sids() {
    [ "$user_sids_loaded" = "1" ] && return 0
    local pdb_output user sid
    pdb_output="$(pdbedit -d0 -L -v 2>/dev/null)" || return 1
    while IFS=$'\t' read -r user sid; do
        [ -n "${user:-}" ] && [ -n "${sid:-}" ] && user_sid["${user,,}"]="$sid"
    done < <(printf '%s\n' "$pdb_output" | awk -F ': *' '
        /^Unix username:/ { user = $2 }
        /^User SID:/      { if (user != "") print user "\t" $2; user = "" }')
    user_sids_loaded=1
}

# Grants exactly the listed users (comma-separated, may be empty = nobody) read and
# write on the share. `access based share enum` hides the share from everyone else
# and smbd refuses their tree-connect. Writes only when the descriptor differs.
apply_share_acl() {
    local name="$1" list="$2" sddl="D:" user sid
    local -a users=()
    load_user_sids || {
        echo "[sync-shares] FAILED to read Samba user SIDs for '$name'." >&2
        return 1
    }
    [ -n "$list" ] && IFS=',' read -r -a users <<< "$list"
    for user in "${users[@]}"; do
        sid="${user_sid[${user,,}]:-}"
        # A user without a Samba account yet cannot connect anyway; sync-users.sh
        # creates it and the next cycle adds the entry.
        [ -n "$sid" ] && sddl+="(A;;FA;;;$sid)"
    done
    [ "$(sharesec -d0 "$name" --viewsddl 2>/dev/null)" = "$sddl" ] && return 0
    sharesec -d0 "$name" -S "$sddl" >/dev/null 2>&1 || {
        echo "[sync-shares] FAILED to set share permissions on '$name'." >&2
        return 1
    }
    [ "$(sharesec -d0 "$name" --viewsddl 2>/dev/null)" = "$sddl" ] || {
        echo "[sync-shares] FAILED verification of share permissions on '$name'." >&2
        return 1
    }
    echo "[sync-shares] share permissions updated: $name (${#users[@]} allowed user(s))"
}

# --- Current state: shares currently in registry (one per line) ---
if ! current_output="$(net conf listshares 2>/dev/null)"; then
    echo "[sync-shares] FAILED to enumerate registry shares." >&2
    exit 1
fi
mapfile -t current < <(printf '%s\n' "$current_output" | sed '/^[[:space:]]*$/d')

# Registry updates affect new TREE_CONNECTs, but an already connected client
# keeps using the service instance (and therefore the old connect path) that
# smbd created for it. Force those clients to reconnect whenever a share is
# removed or its path changes. The new registry state is applied first below,
# so reconnects immediately resolve to the new path.
close_share_sessions() {
    local name="$1"
    if pidof smbd >/dev/null 2>&1; then
        if smbcontrol smbd close-share "$name" >/dev/null 2>&1; then
            echo "[sync-shares] disconnected existing sessions: $name"
        else
            echo "[sync-shares] FAILED to disconnect existing sessions: $name" >&2
            return 1
        fi
    fi
}

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
            net conf delshare "$name" >/dev/null 2>&1 || {
                echo "[sync-shares] FAILED to remove registry share: $name" >&2
                exit 1
            }
            if net conf showshare "$name" >/dev/null 2>&1; then
                echo "[sync-shares] FAILED to verify removal: $name" >&2
                exit 1
            fi
            close_share_sessions "$name" || exit 1
            echo "[sync-shares] removed: $name"
            removed=$((removed + 1))
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
        path_changed=0

        # Samba validates on addshare that the target directory exists.
        mkdir -p -- "$path" || {
            echo "[sync-shares] FAILED to create share directory: $path" >&2
            exit 1
        }
        # Give new share directory to shared storage group + setgid + g+w,
        # so SMB users (group kaimo) and Web (uid $KAIMO_STORAGE_GID) can write
        # and new files inherit the group. Complements create/directory masks
        # in smb.conf.vfs. Idempotent (enforced at each sync).
        chgrp "${KAIMO_STORAGE_GID:-1654}" "$path" 2>/dev/null || {
            echo "[sync-shares] FAILED to set share group: $path" >&2
            exit 1
        }
        chmod 2775 "$path" 2>/dev/null || {
            echo "[sync-shares] FAILED to set share mode: $path" >&2
            exit 1
        }

        # P1-12 immutable close captures live on the same filesystem as the
        # share so the VFS can use a reflink when supported. The namespace is
        # denied by every client-facing VFS path hook. setgid preserves the
        # storage group for bridge reads and post-ack cleanup. Client access to
        # this namespace is blocked by the VFS.
        capture_dir="$path/.kaimo-close-captures"
        if [ -L "$capture_dir" ] ||
           ! install -d -m 2770 -o root -g "${KAIMO_STORAGE_GID:-1654}" -- "$capture_dir"; then
            echo "[sync-shares] FAILED to secure close-capture directory: $capture_dir" >&2
            if net conf showshare "$name" >/dev/null 2>&1; then
                net conf delshare "$name" >/dev/null 2>&1 || true
                close_share_sessions "$name"
            fi
            exit 1
        fi

        # Create, if not already present ... The existing share's parameters are
        # snapshotted with this ONE `net` call: every `net` invocation loads the
        # full Samba configuration (~50 ms) and this runs every few seconds, so
        # per-parameter set/get calls dominated container CPU.
        declare -A share_params=()
        if share_output="$(net conf showshare "$name" 2>/dev/null)"; then
            while IFS= read -r share_line; do
                [[ "$share_line" == $'\t'*' = '* ]] || continue
                share_line="${share_line#$'\t'}"
                share_params["${share_line%% = *}"]="${share_line#* = }"
            done <<< "$share_output"
            current_path="${share_params[path]-}"
            current_canonical="$(readlink -m "$current_path")"
            desired_canonical="$(readlink -m "$path")"
            if [ "$current_canonical" != "$desired_canonical" ]; then
                path_changed=1
            fi
            updated=$((updated + 1))
        else
            net conf addshare "$name" "$path" writeable=y guest_ok=n "Kaimo Share" >/dev/null 2>&1 || {
                echo "[sync-shares] FAILED to create registry share: $name" >&2
                exit 1
            }
            echo "[sync-shares] created: $name -> $path (browseable=$browseable)"
            added=$((added + 1))
        fi

        # ... and in ANY case (new or existing) enforce desired state with
        # CANONICAL Samba parameters. Important: `read only = no` instead of
        # the synonym `writeable` — Samba's default is `read only = yes`, otherwise
        # shares are read-only (reading via SMB works, writing fails at
        # Samba level, before ACL). Hard access control stays at the
        # VFS connect/create_file hook; here only the share base disposition.
        for setting in \
            "path"$'\t'"$path" \
            "read only"$'\t'"no" \
            "browseable"$'\t'"$browseable" \
            "guest ok"$'\t'"no"; do
            param="${setting%%$'\t'*}"
            value="${setting#*$'\t'}"
            # Already in the desired state per the snapshot -> no write needed.
            if [ -n "${share_params[$param]+x}" ]; then
                if [ "$param" = "path" ]; then
                    [ "$(readlink -m "${share_params[$param]}")" = "$(readlink -m "$value")" ] && continue
                elif [ "${share_params[$param]}" = "$value" ]; then
                    continue
                fi
            fi
            net conf setparm "$name" "$param" "$value" >/dev/null 2>&1 || {
                echo "[sync-shares] FAILED to set '$param' on '$name'." >&2
                exit 1
            }
            actual="$(net conf getparm "$name" "$param" 2>/dev/null)" || {
                echo "[sync-shares] FAILED to read back '$param' on '$name'." >&2
                exit 1
            }
            if [ "$param" = "path" ]; then
                [ "$(readlink -m "$actual")" = "$(readlink -m "$value")" ] || {
                    echo "[sync-shares] FAILED verification for '$name/$param'." >&2
                    exit 1
                }
            elif [ "$actual" != "$value" ]; then
                echo "[sync-shares] FAILED verification for '$name/$param': '$actual' != '$value'." >&2
                exit 1
            fi
        done

        access="${want_access[$name]}"
        if [ "$access" != "*" ]; then
            apply_share_acl "$name" "${access#+}" || exit 1
        fi

        if [ "$path_changed" = "1" ]; then
            close_share_sessions "$name" || exit 1
            echo "[sync-shares] path changed: $name -> $path"
        fi
    done
fi

echo "[sync-shares] done: ${#want_path[@]} desired shares (${added} new, ${updated} updated, ${removed} removed)."
exit 0
