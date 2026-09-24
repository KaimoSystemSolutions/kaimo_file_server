#!/bin/sh
# Idempotently bootstraps a dedicated local CA, one bridge server identity, and
# one shared Samba workload identity. The output is split so the bridge never
# sees client/CA private keys and Samba never sees server/CA private keys.
# Production may pre-populate the same layout with externally managed PKI.
set -eu

output="${1:-./secrets/smb-control-plane}"
umask 077
mkdir -p "$output"
bridge_output="$output/bridge"
samba_output="$output/samba"
authority_output="$output/authority"
mkdir -p "$bridge_output" "$samba_output" "$authority_output"

for required in openssl; do
    command -v "$required" >/dev/null 2>&1 || {
        echo "missing required command: $required" >&2
        exit 1
    }
done

issue_certificate() {
    name="$1"
    common_name="$2"
    extended_key_usage="$3"
    subject_alt_name="${4:-}"
    destination="$5"

    openssl req -new -newkey rsa:3072 -sha256 -nodes \
        -subj "/CN=$common_name" \
        -keyout "$destination/$name.key" \
        -out "$authority_output/$name.csr"

    {
        echo "basicConstraints=critical,CA:FALSE"
        echo "keyUsage=critical,digitalSignature,keyEncipherment"
        echo "extendedKeyUsage=$extended_key_usage"
        [ -z "$subject_alt_name" ] || echo "subjectAltName=$subject_alt_name"
    } > "$authority_output/$name.ext"

    openssl x509 -req -sha256 -days 825 \
        -in "$authority_output/$name.csr" \
        -CA "$authority_output/ca.crt" \
        -CAkey "$authority_output/ca.key" \
        -CAcreateserial \
        -extfile "$authority_output/$name.ext" \
        -out "$destination/$name.crt"

    rm -f "$authority_output/$name.csr" "$authority_output/$name.ext"
}

required_files="
$bridge_output/ca.crt
$bridge_output/server.crt
$bridge_output/server.key
$samba_output/ca.crt
$samba_output/samba.crt
$samba_output/samba.key
"

# Leaf keys are readable only by their consumer: Samba's control-plane clients
# (authd and the *sync helpers) run as root; the bridge runs as the .NET app UID.
# Applied on every run so installations created with the former world-readable
# mode (0644) are tightened too. If the bridge key cannot be handed to its UID
# (script not run as root), fall back to the previous mode rather than breaking
# the bridge, and say so.
restrict_leaf_keys() {
    chmod 700 "$authority_output"
    chmod 755 "$bridge_output" "$samba_output"
    chmod 600 "$authority_output/ca.key"
    chmod 644 "$bridge_output"/*.crt "$samba_output"/*.crt
    chmod 600 "$samba_output/samba.key"
    if chown "${KAIMO_BRIDGE_UID:-1654}" "$bridge_output/server.key" 2>/dev/null; then
        chmod 600 "$bridge_output/server.key"
    else
        chmod 644 "$bridge_output/server.key"
        echo "warning: could not assign $bridge_output/server.key to UID ${KAIMO_BRIDGE_UID:-1654}; left world-readable" >&2
    fi
}

missing=0
present=0
for path in $required_files; do
    if [ -f "$path" ] && [ -s "$path" ]; then
        present=$((present + 1))
    else
        missing=$((missing + 1))
    fi
done

if [ "$missing" -eq 0 ]; then
    restrict_leaf_keys
    echo "SMB control-plane PKI already present in $output"
    exit 0
fi

# Migrate PKI produced by the former four-client layout without rotating the
# bridge identity or CA. The old client keys are removed after replacement.
if [ -f "$bridge_output/ca.crt" ] && [ -s "$bridge_output/ca.crt" ] \
    && [ -f "$bridge_output/server.crt" ] && [ -s "$bridge_output/server.crt" ] \
    && [ -f "$bridge_output/server.key" ] && [ -s "$bridge_output/server.key" ] \
    && [ -f "$authority_output/ca.crt" ] && [ -s "$authority_output/ca.crt" ] \
    && [ -f "$authority_output/ca.key" ] && [ -s "$authority_output/ca.key" ] \
    && [ ! -e "$samba_output/samba.crt" ] \
    && [ ! -e "$samba_output/samba.key" ]; then
    issue_certificate samba kaimo-samba clientAuth "" "$samba_output"
    cp "$authority_output/ca.crt" "$samba_output/ca.crt"
else
    if [ "$present" -ne 0 ]; then
        echo "refusing to overwrite incomplete PKI in $output" >&2
        exit 1
    fi

    openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
        -days 3650 \
        -subj "/CN=Kaimo SMB Control Plane CA" \
        -keyout "$authority_output/ca.key" \
        -out "$authority_output/ca.crt"

    issue_certificate server kaimo_smb_bridge serverAuth DNS:kaimo_smb_bridge "$bridge_output"
    issue_certificate samba kaimo-samba clientAuth "" "$samba_output"

    cp "$authority_output/ca.crt" "$bridge_output/ca.crt"
    cp "$authority_output/ca.crt" "$samba_output/ca.crt"
fi

# Remove obsolete credentials from the old layout after successful migration.
rm -f \
    "$samba_output/auth-sync.crt" "$samba_output/auth-sync.key" \
    "$samba_output/share-sync.crt" "$samba_output/share-sync.key" \
    "$samba_output/config-sync.crt" "$samba_output/config-sync.key" \
    "$samba_output/runtime.crt" "$samba_output/runtime.key"

restrict_leaf_keys
echo "SMB control-plane PKI generated in $output"
