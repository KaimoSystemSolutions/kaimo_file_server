#!/bin/sh
# Idempotently bootstraps a dedicated local CA, one server identity, and
# least-privilege client identities for the Samba control plane. The output is
# split so the bridge never sees client/CA private keys and Samba never sees the
# server/CA private keys. Production may pre-populate the same directory layout
# with externally managed certificates; complete existing PKI is left untouched.
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

required_files="
$bridge_output/ca.crt
$bridge_output/server.crt
$bridge_output/server.key
$samba_output/ca.crt
$samba_output/auth-sync.crt
$samba_output/auth-sync.key
$samba_output/share-sync.crt
$samba_output/share-sync.key
$samba_output/config-sync.crt
$samba_output/config-sync.key
$samba_output/runtime.crt
$samba_output/runtime.key
"

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
    echo "SMB control-plane PKI already present in $output"
    exit 0
fi

if [ "$present" -ne 0 ]; then
    echo "refusing to overwrite incomplete PKI in $output" >&2
    exit 1
fi

openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
    -days 3650 \
    -subj "/CN=Kaimo SMB Control Plane CA" \
    -keyout "$authority_output/ca.key" \
    -out "$authority_output/ca.crt"

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

issue_certificate server kaimo_smb_bridge serverAuth DNS:kaimo_smb_bridge "$bridge_output"
issue_certificate auth-sync kaimo-auth-sync clientAuth "" "$samba_output"
issue_certificate share-sync kaimo-share-sync clientAuth "" "$samba_output"
issue_certificate config-sync kaimo-config-sync clientAuth "" "$samba_output"
issue_certificate runtime kaimo-samba-runtime clientAuth "" "$samba_output"

cp "$authority_output/ca.crt" "$bridge_output/ca.crt"
cp "$authority_output/ca.crt" "$samba_output/ca.crt"

# Leaf keys must be readable by the unprivileged processes in their narrowly
# scoped read-only mounts. The CA key remains owner-only and is never mounted.
chmod 700 "$authority_output"
chmod 755 "$bridge_output" "$samba_output"
chmod 600 "$authority_output/ca.key"
chmod 644 "$bridge_output/server.key" \
    "$samba_output/auth-sync.key" "$samba_output/share-sync.key" \
    "$samba_output/config-sync.key" "$samba_output/runtime.key"
chmod 644 "$bridge_output"/*.crt "$samba_output"/*.crt
echo "SMB control-plane PKI generated in $output"
