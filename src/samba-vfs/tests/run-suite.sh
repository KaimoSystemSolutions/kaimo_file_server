#!/bin/bash
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
#
# Complete samba-vfs test suite with coverage, run inside the Dockerfile.vfs
# `coverage` stage (never in a production image):
#
#   1. C++ component tests       ASan/UBSan + gcov
#   2. sidecars (authd, *sync)   rebuilt with gcov into /usr/local/bin
#   3. vfs_kaimo_bridge.so       relinked with gcov into /opt/samba
#   4. shell reconciler tests    every /usr/local/bin/*.sh wrapped by kcov
#   5. live VFS/authd suite      pytest against real smbd + fake authd
#   6. Python helper tests       coverage.py
#   7. reports + thresholds      gcovr, kcov, coverage.py -> $OUT
#
# Test failures are collected instead of aborting, so reports are always
# produced; $OUT/status.txt names every failed step. The script exits non-zero
# on any failure unless KAIMO_SUITE_SOFT_FAIL=1 (CI exports the reports first
# and then fails the job from status.txt).
set -euo pipefail

SUITE="${KAIMO_SUITE_ROOT:-/opt/kaimo-tests}"
TESTS="$SUITE/tests"
MODULE="$SUITE/module"
SCRIPTS="$SUITE/scripts"
OUT="${KAIMO_COVERAGE_DIR:-/coverage}"
SAMBA_SOURCE="${KAIMO_SAMBA_SOURCE:-/build/samba-source}"
PROTO_BUILD="${KAIMO_PROTO_BUILD:-/build/authsync}"
KCOV="${KAIMO_KCOV:-/opt/kcov/bin/kcov}"
# Scripts whose tests deliver signals to the script PID or inspect its own
# descriptors; a tracing parent process would change what they observe.
KCOV_EXCLUDE="${KAIMO_KCOV_EXCLUDE:-supervise-samba.sh}"

GCOV_BUILD="$OUT/build"
FAILED=()
# attempt <name> <command...>: run a test step, record (not abort on) failure.
attempt() {
    local name="$1"
    shift
    echo "-- $name"
    if ! "$@"; then
        echo "!! FAILED: $name"
        FAILED+=("$name")
    fi
}
step() { printf '\n=== %s ===\n' "$*"; }

rm -rf "$OUT"
mkdir -p "$GCOV_BUILD/cpp" "$GCOV_BUILD/sidecars" "$OUT/kcov/runs" "$OUT/python" "$OUT/junit"

# ---------------------------------------------------------------------------
step "1. C++ component tests (ASan/UBSan + gcov)"
CXX_TEST_FLAGS=(-std=c++17 -O0 -g --coverage -fsanitize=address,undefined
    -fno-omit-frame-pointer -fno-sanitize-recover=all -Wall -Wextra -pthread
    "-I$MODULE" "-I$TESTS")
for source in "$TESTS"/test-*.cpp; do
    name="$(basename "$source" .cpp)"
    attempt "$name" bash -c 'g++ "${@:3}" "$1" -o "$2" && cd "$(dirname "$2")" && ASAN_OPTIONS=detect_leaks=1 "$2"' \
        _ "$source" "$GCOV_BUILD/cpp/$name" "${CXX_TEST_FLAGS[@]}"
done

# ---------------------------------------------------------------------------
step "2. Sidecars with gcov"
GRPC_FLAGS=($(pkg-config --cflags grpc++ protobuf))
GRPC_LIBS=($(pkg-config --libs grpc++ protobuf))
for generated in kaimo_smb_bridge.pb.cc kaimo_smb_bridge.grpc.pb.cc; do
    g++ -std=c++17 -O1 "-I$PROTO_BUILD" "${GRPC_FLAGS[@]}" \
        -c "$PROTO_BUILD/$generated" -o "$GCOV_BUILD/sidecars/$generated.o"
done
for sidecar in authd authsync sharesync configsync; do
    echo "-- kaimo_$sidecar"
    # Separate compile step: .gcno/.gcda land next to the object in $GCOV_BUILD.
    g++ -std=c++17 -O0 -g --coverage -pthread "-I$MODULE" "-I$PROTO_BUILD" \
        -include "$TESTS/tools/gcov_flush_on_signal.h" \
        "${GRPC_FLAGS[@]}" -c "$MODULE/$sidecar.cpp" \
        -o "$GCOV_BUILD/sidecars/$sidecar.o"
    g++ --coverage -pthread "$GCOV_BUILD/sidecars/$sidecar.o" \
        "$GCOV_BUILD/sidecars/kaimo_smb_bridge.pb.cc.o" \
        "$GCOV_BUILD/sidecars/kaimo_smb_bridge.grpc.pb.cc.o" \
        "${GRPC_LIBS[@]}" -o "/usr/local/bin/kaimo_$sidecar"
done
# Sidecar children may run unprivileged; allow them to write .gcda files.
chmod -R a+rwX "$GCOV_BUILD"

# ---------------------------------------------------------------------------
step "3. VFS module with gcov"
python3 "$TESTS/tools/build-vfs-module-coverage.py" "$SAMBA_SOURCE" "$MODULE"

# ---------------------------------------------------------------------------
step "4. Shell tests under kcov"
# Every installed script becomes a kcov wrapper around the pristine copy in
# $SCRIPTS, so nested invocations (run-sync -> sync-users, ...) are traced too.
# "installed path:source name" (mirrors the COPY lines of Dockerfile.vfs).
WRAPPED=(
    /usr/local/bin/entrypoint.sh:entrypoint.vfs.sh
    /tmp/phase0-entrypoint.sh:entrypoint.sh
    /tmp/verify-samba-build.sh:verify-samba-build.sh
    /usr/local/bin/generate-control-plane-certs.sh:generate-control-plane-certs.sh
)
for real in "$SCRIPTS"/*.sh; do
    name="$(basename "$real")"
    case "$name" in entrypoint.sh|entrypoint.vfs.sh|verify-samba-build.sh|generate-control-plane-certs.sh) continue ;; esac
    [ -e "/usr/local/bin/$name" ] && WRAPPED+=("/usr/local/bin/$name:$name")
done
for mapping in "${WRAPPED[@]}"; do
    installed="${mapping%%:*}"
    name="${mapping##*:}"
    real="$SCRIPTS/$name"
    case " $KCOV_EXCLUDE " in *" $name "*) cp "$real" "$installed"; chmod 0755 "$installed"; continue ;; esac
    cat > "$installed" <<EOF
#!/bin/bash
exec "$KCOV" --include-path="$SCRIPTS" --bash-dont-parse-binary-dir --bash-tracefd-cloexec \\
    "$OUT/kcov/runs/$name.\$\$.\$RANDOM" "$real" "\$@"
EOF
    chmod 0755 "$installed"
done

run_shell_test() { attempt "$@"; }
BIN=/usr/local/bin
run_shell_test sync-users env SYNC_USERS_SUT="$BIN/sync-users.sh" bash "$TESTS/test-sync-users.sh"
run_shell_test sync-shares env SYNC_SHARES_SUT="$BIN/sync-shares.sh" bash "$TESTS/test-sync-shares.sh"
run_shell_test sync-config env SYNC_CONFIG_SUT="$BIN/sync-config.sh" bash "$TESTS/test-sync-config.sh"
run_shell_test sync-config-registry env SYNC_CONFIG_SUT="$BIN/sync-config.sh" \
    SAMBA_NET_BIN=/opt/samba/bin/net bash "$TESTS/test-sync-config-samba-registry.sh"
run_shell_test sync-runner env SYNC_RUNNER_SUT="$BIN/run-sync.sh" \
    SYNC_HEALTH_SUT="$BIN/sync-health.sh" bash "$TESTS/test-sync-runner.sh"
run_shell_test revocation-policy env SESSION_REVOKER_SUT="$BIN/revoke-samba-sessions.sh" \
    SYNC_CYCLE_SUT="$BIN/sync-cycle.sh" \
    SYNC_INTERVAL_VALIDATOR_SUT="$BIN/validate-sync-interval.sh" \
    bash "$TESTS/test-revocation-policy.sh"
run_shell_test authd-supervisor env AUTHD_SUPERVISOR_SUT="$BIN/supervise-samba.sh" \
    AUTHD_HEALTH_SUT="$BIN/authd-health.sh" SMBD_HEALTH_SUT="$BIN/smbd-health.sh" \
    bash "$TESTS/test-authd-supervisor.sh"
# This test greps the script sources statically: hand it the real files, not
# the kcov wrappers (a wrapper contains none of the searched patterns).
run_shell_test operational-credentials env CREDENTIAL_ENTRYPOINT_SUT="$SCRIPTS/entrypoint.vfs.sh" \
    CREDENTIAL_PHASE0_ENTRYPOINT_SUT="$SCRIPTS/entrypoint.sh" \
    CREDENTIAL_SELFTEST_SUT="$SCRIPTS/selftest.sh" \
    CREDENTIAL_SYNC_USERS_SUT="$SCRIPTS/sync-users.sh" \
    CREDENTIAL_SYNC_CONFIG_SUT="$SCRIPTS/sync-config.sh" \
    bash "$TESTS/test-operational-credentials.sh"
run_shell_test event-spool-permissions env EVENT_SPOOL_PREPARER_SUT="$BIN/prepare-event-spool.sh" \
    bash "$TESTS/test-event-spool-permissions.sh"
for extra in "$TESTS"/shell/test-*.sh; do
    [ -e "$extra" ] || continue
    run_shell_test "$(basename "$extra" .sh)" env KAIMO_SCRIPTS_DIR="$BIN" \
        KAIMO_SCRIPT_SOURCES="$SCRIPTS" bash "$extra"
done

# ---------------------------------------------------------------------------
step "5. Live VFS / authd suite (pytest)"
# authd trusts a root peer only if its process name matches the configured
# executable's basename; run as the resolved interpreter (python3.12, not the
# python3 symlink) so the test process is a valid "smbd" stand-in.
PYTHON="$(readlink -f "$(command -v python3)")"
attempt live-suite "$PYTHON" -m pytest -p no:cacheprovider -q -rfE \
    --junitxml="$OUT/junit/live.xml" "$TESTS/live"

# ---------------------------------------------------------------------------
step "6. Python helper tests (coverage.py)"
if compgen -G "$TESTS/python/test_*.py" >/dev/null; then
    attempt python-helpers env KAIMO_SCRIPT_SOURCES="$SCRIPTS" python3 -m coverage run --branch \
        --data-file="$OUT/python/.coverage" --include="$SCRIPTS/*" \
        -m pytest -p no:cacheprovider -q -rfE \
        --junitxml="$OUT/junit/python.xml" "$TESTS/python"
    python3 -m coverage json --data-file="$OUT/python/.coverage" -o "$OUT/python/coverage.json"
    python3 -m coverage html --data-file="$OUT/python/.coverage" -d "$OUT/python/html"
fi

# ---------------------------------------------------------------------------
step "7. Reports"
mkdir -p "$OUT/gcovr"
gcovr --root "$SUITE" \
    --filter "$MODULE/" \
    --exclude '.*\.pb\.(h|cc)$' \
    --exclude-unreachable-branches --exclude-throw-branches \
    --gcov-ignore-parse-errors=negative_hits.warn_once_per_file \
    --html-details "$OUT/gcovr/index.html" \
    --cobertura "$OUT/gcovr/cobertura.xml" \
    --json-summary "$OUT/gcovr/summary.json" \
    --txt "$OUT/gcovr/summary.txt" \
    "$GCOV_BUILD" "$SAMBA_SOURCE/bin/default/source3/modules"
cat "$OUT/gcovr/summary.txt"

runs=("$OUT"/kcov/runs/*/)
if [ -d "${runs[0]:-}" ]; then
    "$KCOV" --merge "$OUT/kcov/merged" "${runs[@]}"
    cp "$OUT/kcov/merged/kcov-merged/cobertura.xml" "$OUT/kcov/cobertura.xml"
fi
rm -rf "$OUT/kcov/runs"

attempt coverage-gates python3 "$TESTS/tools/check-coverage.py" "$TESTS/quality-gates.json" "$OUT"

# The exported report tree should not carry the object files and gcov notes
# the reports were built from.
rm -rf "$GCOV_BUILD"

if [ "${#FAILED[@]}" -eq 0 ]; then
    echo "passed" > "$OUT/status.txt"
    step "Suite passed"
    exit 0
fi
printf 'failed: %s\n' "${FAILED[*]}" > "$OUT/status.txt"
step "Suite FAILED: ${FAILED[*]}"
[ "${KAIMO_SUITE_SOFT_FAIL:-0}" = 1 ] && exit 0
exit 1
