#!/bin/bash
# Container entrypoint (entrypoint.vfs.sh): storage group setup, bounded
# initial convergence, interval validation, periodic fail-closed revocation
# and the hand-off to the supervisor. Every helper it calls is replaced via
# KAIMO_ENTRYPOINT_SCRIPT_DIR; prepare-event-spool.sh and
# validate-sync-interval.sh are the real scripts.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
if [ -n "${ENTRYPOINT_VFS_SUT:-}" ]; then
    ENTRYPOINT="$ENTRYPOINT_VFS_SUT"
elif [ -e "$SCRIPTS/entrypoint.vfs.sh" ]; then
    ENTRYPOINT="$SCRIPTS/entrypoint.vfs.sh"
else
    ENTRYPOINT="$SCRIPTS/entrypoint.sh"  # installed name in the image
fi

WORK="$(mktemp -d)"
chmod 0755 "$WORK"
SUFFIX="$$"
STORAGE_GID=$((42000 + SUFFIX % 1000))
STORAGE_GROUP="ktstore$SUFFIX"
AUTHD_GROUP="ktauthd$SUFFIX"
UNIT_PID=""
cleanup() {
    [ -n "$UNIT_PID" ] && kill "$UNIT_PID" 2>/dev/null
    groupdel "$STORAGE_GROUP" 2>/dev/null
    groupdel "$AUTHD_GROUP" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT
fail=0
check() {
    if eval "$2"; then
        echo "  ok: $1"
    else
        echo "FAIL: $1"
        echo "----- entrypoint output -----"
        cat "$WORK/out" 2>/dev/null
        fail=1
    fi
}

# ---------------------------------------------------------------- helpers
mkdir "$WORK/bin"
cat > "$WORK/bin/prepare-event-spool.sh" <<EOF
#!/bin/bash
exec bash "$SCRIPTS/prepare-event-spool.sh" "\$@"
EOF
cat > "$WORK/bin/validate-sync-interval.sh" <<EOF
#!/bin/bash
exec bash "$SCRIPTS/validate-sync-interval.sh" "\$@"
EOF
cat > "$WORK/bin/run-sync.sh" <<'EOF'
#!/bin/bash
# Initial convergence stub: optionally fail one component N times.
echo "$1 $2" >> "$WORK_DIR/run-sync.log"
if [ "$1" = "${RUN_SYNC_FAIL_COMPONENT:-}" ]; then
    count=$(( $(cat "$WORK_DIR/failures" 2>/dev/null || echo 0) + 1 ))
    echo "$count" > "$WORK_DIR/failures"
    [ "$count" -gt "${RUN_SYNC_FAIL_TIMES:-0}" ] || exit 1
fi
exit 0
EOF
cat > "$WORK/bin/sync-cycle.sh" <<'EOF'
#!/bin/bash
echo "$1" >> "$WORK_DIR/sync-cycle.log"
[ "$1" != "${CYCLE_FAIL_COMPONENT:-}" ]
EOF
cat > "$WORK/bin/supervise-samba.sh" <<'EOF'
#!/bin/bash
{
    echo "KAIMO_AUTHD_SOCK=$KAIMO_AUTHD_SOCK"
    echo "KAIMO_AUTHD_GROUP=$KAIMO_AUTHD_GROUP"
    echo "KAIMO_STORAGE_GROUP=$KAIMO_STORAGE_GROUP"
    echo "KAIMO_STORAGE_GID=$KAIMO_STORAGE_GID"
} > "$WORK_DIR/supervisor.env"
sleep "${SUPERVISOR_SECONDS:-0}"
touch "$WORK_DIR/supervisor.done"
EOF
chmod +x "$WORK/bin"/*.sh

reset_run() {
    rm -rf "$WORK/storage" "$WORK/spool" "$WORK"/*.log "$WORK"/supervisor.* \
        "$WORK/failures" "$WORK/out"
    mkdir -p "$WORK/storage/share1/sub" "$WORK/storage/.private"
}

# Runs the entrypoint in its own session so periodic sync loops left behind by
# the exec'd supervisor can be reaped. Sets $rc unless DETACH=1.
start_entrypoint() {
    env WORK_DIR="$WORK" \
        KAIMO_ENTRYPOINT_SCRIPT_DIR="$WORK/bin" \
        KAIMO_STORAGE="$WORK/storage" \
        KAIMO_EVENT_SPOOL_PATH="$WORK/spool" \
        KAIMO_STORAGE_GID="$STORAGE_GID" \
        KAIMO_STORAGE_GROUP="$STORAGE_GROUP" \
        KAIMO_AUTHD_GROUP="$AUTHD_GROUP" \
        KAIMO_INITIAL_SYNC_RETRY_SECONDS=0 \
        "$@" setsid bash "$ENTRYPOINT" > "$WORK/out" 2>&1 &
    session=$!
}
stop_session() {
    # End the periodic `while sleep` loops gracefully (a failed sleep ends the
    # loop) so a tracing parent such as kcov can still write its results.
    local tries=0
    while kill -0 "$session" 2>/dev/null && [ "$tries" -lt 100 ]; do
        pkill -TERM -s "$session" -x sleep 2>/dev/null
        sleep 0.1
        tries=$((tries + 1))
    done
    pkill -KILL -s "$session" 2>/dev/null
    wait "$session" 2>/dev/null
}
wait_for_file() {
    local path="$1" tries=0
    while [ ! -e "$path" ] && [ "$tries" -lt 150 ]; do
        sleep 0.1
        tries=$((tries + 1))
    done
    [ -e "$path" ]
}

# ---------------------------------------------------------------- happy path
reset_run
start_entrypoint KAIMO_SHARE_SYNC_INTERVAL_SECONDS=1 KAIMO_CONFIG_SYNC_INTERVAL_SECONDS=1 \
    SUPERVISOR_SECONDS=2.5
wait_for_file "$WORK/supervisor.done"
stop_session
check "initial sync converges users, shares, config in order" \
    '[ "$(cut -d" " -f1 "$WORK/run-sync.log" | tr "\n" " ")" = "users shares config " ]'
check "initial sync passes the installed reconcilers" \
    'grep -q "^users $WORK/bin/sync-users.sh$" "$WORK/run-sync.log"'
check "supervisor receives the authorization socket" \
    'grep -qx "KAIMO_AUTHD_SOCK=/var/run/kaimo/authz.sock" "$WORK/supervisor.env"'
check "storage group created and exported" \
    'grep -qx "KAIMO_STORAGE_GROUP=$STORAGE_GROUP" "$WORK/supervisor.env" && [ "$(getent group "$STORAGE_GID" | cut -d: -f1)" = "$STORAGE_GROUP" ]'
check "authd socket group created" 'getent group "$AUTHD_GROUP" >/dev/null'
check "share directories get the storage group and setgid" \
    '[ "$(stat -c %g "$WORK/storage/share1/sub")" = "$STORAGE_GID" ] && [ -g "$WORK/storage/share1" ] && [ -g "$WORK/storage/share1/sub" ]'
check "internal dot directories are left alone" \
    '[ "$(stat -c %g "$WORK/storage/.private")" = "0" ] && [ ! -g "$WORK/storage/.private" ]'
check "event spool prepared" '[ -d "$WORK/spool" ] && [ "$(stat -c %a "$WORK/spool")" = "700" ]'
check "periodic share and config cycles run" \
    'grep -qx shares "$WORK/sync-cycle.log" && grep -qx config "$WORK/sync-cycle.log"'
check "storage ACL mode is reported" 'grep -q "\[entrypoint\] Storage:" "$WORK/out"'
check "authd socket directory is private to its group" \
    '[ "$(stat -c "%a %G" /var/run/kaimo)" = "750 $AUTHD_GROUP" ]'

# -------------------------------------------- existing storage group (GID 0)
reset_run
STORAGE_GID_SAVED="$STORAGE_GID"
STORAGE_GID=0
start_entrypoint KAIMO_SHARE_SYNC_INTERVAL_SECONDS=5 KAIMO_CONFIG_SYNC_INTERVAL_SECONDS=5
wait_for_file "$WORK/supervisor.done"
stop_session
STORAGE_GID="$STORAGE_GID_SAVED"
check "an existing storage GID keeps its group name" \
    'grep -qx "KAIMO_STORAGE_GROUP=root" "$WORK/supervisor.env"'

# ------------------------------------------- convergence after transient failures
reset_run
start_entrypoint KAIMO_INITIAL_SYNC_ATTEMPTS=5 RUN_SYNC_FAIL_COMPONENT=shares RUN_SYNC_FAIL_TIMES=2
wait_for_file "$WORK/supervisor.done"
stop_session
check "shares retried until converged" '[ "$(grep -c "^shares " "$WORK/run-sync.log")" = "3" ]'
check "supervisor starts after late convergence" '[ -e "$WORK/supervisor.env" ]'

# ------------------------------------------------------- no convergence at all
reset_run
start_entrypoint KAIMO_INITIAL_SYNC_ATTEMPTS=3 RUN_SYNC_FAIL_COMPONENT=shares RUN_SYNC_FAIL_TIMES=99
wait "$session"
rc=$?
check "entrypoint refuses to start without convergence" '[ "$rc" -ne 0 ]'
check "bounded number of attempts" '[ "$(grep -c "^shares " "$WORK/run-sync.log")" = "3" ]'
check "later components are not synced" '! grep -q "^config " "$WORK/run-sync.log"'
check "supervisor never started" '[ ! -e "$WORK/supervisor.env" ]'
check "refusal is explained" 'grep -q "shares did not converge; refusing to start Samba" "$WORK/out"'

# ------------------------------------------------------- invalid sync interval
for invalid in KAIMO_USER_SYNC_INTERVAL_SECONDS=30 KAIMO_SHARE_SYNC_INTERVAL_SECONDS=9 \
    KAIMO_CONFIG_SYNC_INTERVAL_SECONDS=0; do
    reset_run
    start_entrypoint "$invalid"
    wait "$session"
    rc=$?
    check "invalid $invalid stops before any sync" \
        '[ "$rc" -ne 0 ] && [ ! -e "$WORK/run-sync.log" ] && [ ! -e "$WORK/supervisor.env" ]'
done

# ------------------------------------------------ periodic revocation failure
reset_run
sleep 60 &
UNIT_PID=$!
start_entrypoint KAIMO_SHARE_SYNC_INTERVAL_SECONDS=1 KAIMO_CONFIG_SYNC_INTERVAL_SECONDS=1 \
    KAIMO_ENTRYPOINT_UNIT_PID="$UNIT_PID" CYCLE_FAIL_COMPONENT=config SUPERVISOR_SECONDS=4
tries=0
while kill -0 "$UNIT_PID" 2>/dev/null && [ "$tries" -lt 60 ]; do
    sleep 0.1
    tries=$((tries + 1))
done
check "failed revocation terminates the Samba unit" '! kill -0 "$UNIT_PID" 2>/dev/null'
wait "$UNIT_PID" 2>/dev/null
UNIT_PID=""
stop_session
check "fatal revocation failure is reported" \
    'grep -q "Fatal config revocation failure; terminating Samba unit" "$WORK/out"'

if [ "$fail" -ne 0 ]; then
    exit 1
fi
echo "PASS: entrypoint converges, validates and fails closed before Samba starts."
