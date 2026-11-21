# Steady-state fast path shared by sync-shares.sh and sync-config.sh (sourced,
# not executed).
#
# Both reconcilers run every few seconds, and a full pass spawns several Samba
# tools that each load the complete configuration. run-sync.sh hands every
# reconciler a private record of its last verified pass (KAIMO_SYNC_STATE_FILE,
# serialized by the runner lock). A cycle is skipped when the desired state is
# byte-identical and no local input changed. Records expire after
# KAIMO_SYNC_FULL_RECONCILE_SECONDS (default 60; 0 or invalid = always full) as
# a safety net for drift a file stamp cannot reveal; the entrypoint discards
# them on container start.
#
# Record layout: line 1 = epoch seconds, line 2 = fingerprint, then one watched
# directory per line (desired paths never contain control characters).

APPLIED_STATE_FILE="${KAIMO_SYNC_STATE_FILE:-}"
APPLIED_STATE_MAX_AGE="${KAIMO_SYNC_FULL_RECONCILE_SECONDS:-60}"
[[ "$APPLIED_STATE_MAX_AGE" =~ ^[0-9]{1,6}$ ]] || APPLIED_STATE_MAX_AGE=0

# applied_state_fingerprint <desired> <files, colon-separated> [dir...]
# Files carry their full stat including timestamps; directories carry no
# timestamps because client writes change them constantly.
applied_state_fingerprint() {
    local desired="$1"
    local -a files=()
    IFS=':' read -r -a files <<<"$2"
    shift 2
    {
        printf '%s\n' "$desired"
        if (( ${#files[@]} > 0 )); then
            LC_ALL=C stat -c '%n|%F|%a|%u|%g|%i|%s|%.9Y|%.9Z' -- "${files[@]}" 2>&1
        fi
        if (( $# > 0 )); then
            LC_ALL=C stat -c '%n|%F|%a|%u|%g|%i' -- "$@" 2>&1
        fi
    } | sha256sum
}

# applied_state_unchanged <desired> <files>: succeeds only for a fresh record
# whose fingerprint still matches the current desired state and local inputs.
applied_state_unchanged() {
    [ -n "$APPLIED_STATE_FILE" ] && (( APPLIED_STATE_MAX_AGE > 0 )) || return 1
    local recorded_at="" recorded_hash="" now
    local -a recorded_dirs=()
    { read -r recorded_at && read -r recorded_hash && mapfile -t recorded_dirs; } \
        2>/dev/null <"$APPLIED_STATE_FILE"
    printf -v now '%(%s)T' -1
    [[ "$recorded_at" =~ ^[0-9]{1,12}$ ]] \
        && (( now >= recorded_at && now - recorded_at < APPLIED_STATE_MAX_AGE )) \
        && [ "$(applied_state_fingerprint "$1" "$2" "${recorded_dirs[@]}")" = "$recorded_hash" ]
}

# Never leave a record behind that a failed or partial pass could match:
# callers discard it before their first mutation.
applied_state_discard() {
    [ -z "$APPLIED_STATE_FILE" ] || rm -f -- "$APPLIED_STATE_FILE"
}

# applied_state_record <desired> <files> [dir...]: called after a verified pass.
applied_state_record() {
    [ -n "$APPLIED_STATE_FILE" ] || return 0
    local now fingerprint tmp
    fingerprint="$(applied_state_fingerprint "$@")" || return 1
    shift 2
    printf -v now '%(%s)T' -1
    tmp="$(mktemp "$APPLIED_STATE_FILE.XXXXXX")" || return 1
    if printf '%s\n%s\n' "$now" "$fingerprint" >"$tmp" \
        && { (( $# == 0 )) || printf '%s\n' "$@" >>"$tmp"; } \
        && mv -f -- "$tmp" "$APPLIED_STATE_FILE"; then
        return 0
    fi
    rm -f -- "$tmp"
    return 1
}
