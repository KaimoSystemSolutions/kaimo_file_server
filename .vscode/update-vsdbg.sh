#!/usr/bin/env bash
# Linux counterpart of update-vsdbg.ps1 (used by the VS Code tasks on Linux hosts); keep both
# in sync. Installs the Linux x64 vsdbg that belongs to the installed VS Code C# extension
# into ~/vsdbg/vscode, which docker-compose.debug.yml mounts into the containers.
# The container's vsdbg must be the same release as the extension's own debugger
# (vsdbg-ui); an older one rejects every breakpoint with "Incorrect breakpoint request
# format". The public "latest" vsdbg lags behind the extension, so the package URL and
# SHA-256 are taken from the extension's package.json. Offline, an already installed copy
# is kept.
#
# --copy-to "a,b,c": additionally copies it to /tmp/vsdbg in those of the containers that
# are running, for the "Attach ..." configurations against a stack started without the
# debug overlay.
#
# Requires: docker, curl, unzip, jq, sha256sum.
set -euo pipefail

copy_to=''
while [[ $# -gt 0 ]]; do
    case "$1" in
        --copy-to) copy_to="${2:-}"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

target="$HOME/vsdbg/vscode"
marker="$target/kaimo-source-url.txt"

for tool in docker curl unzip jq sha256sum; do
    command -v "$tool" >/dev/null || { echo "Required tool '$tool' is not installed." >&2; exit 1; }
done

read_marker() {
    [[ -f "$marker" ]] && tr -d '[:space:]' < "$marker" || true
}

install_vsdbg() {
    local url="$1" integrity="$2"
    [[ "$(read_marker)" == "$url" ]] && return 0

    local zip
    zip="$(mktemp --suffix=.zip)"
    if ! curl -fsSL "$url" -o "$zip"; then
        rm -f "$zip"
        if [[ -x "$target/vsdbg" ]]; then
            echo "WARNING: vsdbg download failed, keeping the installed copy." >&2
            return 0
        fi
        echo "vsdbg download failed: $url" >&2
        exit 1
    fi
    local hash
    hash="$(sha256sum "$zip" | cut -d' ' -f1)"
    if [[ "${hash,,}" != "${integrity,,}" ]]; then
        rm -f "$zip"
        echo 'vsdbg package checksum does not match the C# extension.' >&2
        exit 1
    fi

    # Containers still using the old copy must stop first (Docker Desktop keeps the old
    # memory-mapped pages of a replaced file); the following `up -d` restarts them.
    local running
    running="$(docker ps -q --filter volume=/remote_debugger)"
    if [[ -n "$running" ]]; then
        # shellcheck disable=SC2086 # word splitting of the container ids is intended
        docker stop $running >/dev/null
    fi

    # Keep the folder itself: it is the bind-mount source.
    mkdir -p "$target"
    find "$target" -mindepth 1 -delete
    unzip -q "$zip" -d "$target"
    rm -f "$zip"
    chmod +x "$target/vsdbg"
    printf '%s' "$url" > "$marker"
    echo 'Installed vsdbg.'
}

copy_vsdbg() {
    local container="$1" url="$2"
    [[ -n "$(docker ps -q --filter "name=^${container}\$")" ]] || return 0
    # Copied once per container and vsdbg release; a recreated container gets it again.
    local installed
    installed="$(docker exec "$container" sh -c 'cat /tmp/vsdbg/kaimo-source-url.txt 2>/dev/null' || true)"
    [[ "$(tr -d '[:space:]' <<< "$installed")" == "$url" ]] && return 0
    docker exec -u 0 "$container" rm -rf /tmp/vsdbg
    docker cp "$target" "${container}:/tmp/vsdbg" || { echo "Copying vsdbg into $container failed." >&2; exit 1; }
    echo "Copied vsdbg into $container."
}

# Newest C# extension; ~/.vscode-server covers VS Code Remote (SSH/WSL) hosts.
extension_roots=()
for root in "$HOME/.vscode/extensions" "$HOME/.vscode-server/extensions"; do
    [[ -d "$root" ]] && extension_roots+=("$root")
done
[[ ${#extension_roots[@]} -gt 0 ]] || { echo 'No VS Code extensions folder found.' >&2; exit 1; }
extension="$(
    find "${extension_roots[@]}" -maxdepth 1 -type d -name 'ms-dotnettools.csharp-*' |
    awk -F/ '{ v = $NF; sub(/^ms-dotnettools\.csharp-/, "", v); sub(/[^0-9.].*$/, "", v); print v "\t" $0 }' |
    sort -V -r -k1,1 | head -n1 | cut -f2-
)"
[[ -n "$extension" ]] || { echo 'VS Code C# extension (ms-dotnettools.csharp) not found.' >&2; exit 1; }

debugger="$(jq -r '
    [.runtimeDependencies[]
     | select(.id == "Debugger" and (.platforms | index("linux")) and (.architectures | index("x86_64")))]
    | first | select(. != null) | "\(.url)\t\(.integrity)"' "$extension/package.json")"
[[ -n "$debugger" ]] || { echo "No Linux x64 debugger package listed in $(basename "$extension")." >&2; exit 1; }

install_vsdbg "${debugger%%$'\t'*}" "${debugger#*$'\t'}"
installed_url="$(read_marker)"
IFS=',' read -r -a containers <<< "$copy_to"
for container in "${containers[@]}"; do
    container="$(tr -d '[:space:]' <<< "$container")"
    [[ -n "$container" ]] && copy_vsdbg "$container" "$installed_url"
done
echo "vsdbg for $(basename "$extension") is ready."
