// kaimo_configsync - gRPC C++ client for protocol settings (Phase 4).
//
// Calls GetProtocolSettings on the .NET bridge over mTLS and emits one
// versioned JSON document.
// The mapping to Samba (net conf setparm global, wsdd daemon, full_audit VFS) is
// handled by the shell script sync-config.sh. Mirror to kaimo_authsync / kaimo_sharesync.
// The enabled flag (Phase 5) is informational here — the authoritative on/off gate
// is the bridge's AuthorizeConnect (deny-all when disabled), enforced in the VFS
// connect hook. wsdd/audit drive their own Samba mechanisms in sync-config.sh.
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "kaimo_smb_bridge.grpc.pb.h"
#include "sync_json.h"

using kaimo::smb::bridge::v1::ConfigService;
using kaimo::smb::bridge::v1::GetProtocolSettingsRequest;
using kaimo::smb::bridge::v1::ProtocolSettingsReply;

int main() {
    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";

    std::shared_ptr<grpc::Channel> channel;
    try {
        channel = kaimo::control_plane::create_mtls_channel(
            addr,
            "KAIMO_BRIDGE_CONFIG_SYNC_CERT",
            "/run/secrets/kaimo-control-plane/samba.crt",
            "KAIMO_BRIDGE_CONFIG_SYNC_KEY",
            "/run/secrets/kaimo-control-plane/samba.key");
    } catch (const std::exception& error) {
        std::cerr << "kaimo_configsync: " << error.what() << std::endl;
        return 1;
    }
    std::unique_ptr<ConfigService::Stub> stub = ConfigService::NewStub(channel);

    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));

    GetProtocolSettingsRequest req;
    ProtocolSettingsReply reply;
    grpc::Status status = stub->GetProtocolSettings(&ctx, req, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_configsync: GetProtocolSettings RPC failed: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return 1;
    }

    const int min_rank = kaimo::sync_json::dialect_rank(reply.min_protocol());
    const int max_rank = kaimo::sync_json::dialect_rank(reply.max_protocol());
    if (min_rank < 0 || max_rank < 0 || min_rank > max_rank) {
        std::cerr << "kaimo_configsync: invalid protocol range." << std::endl;
        return 1;
    }

    std::cout << "{\"version\":1,\"config\":{"
              << "\"min_protocol\":" << kaimo::sync_json::quote(reply.min_protocol())
              << ",\"max_protocol\":" << kaimo::sync_json::quote(reply.max_protocol())
              << ",\"require_signing\":" << kaimo::sync_json::boolean(reply.require_signing())
              << ",\"require_encryption\":" << kaimo::sync_json::boolean(reply.require_encryption())
              << ",\"enabled\":" << kaimo::sync_json::boolean(reply.enabled())
              << ",\"enable_ws_discovery\":" << kaimo::sync_json::boolean(reply.enable_ws_discovery())
              << ",\"enable_audit_log\":" << kaimo::sync_json::boolean(reply.enable_audit_log())
              << "}}\n";
    std::cerr << "kaimo_configsync: settings received (addr=" << addr << ")." << std::endl;
    return 0;
}
