// kaimo_configsync - gRPC C++ client for protocol settings (Phase 4).
//
// Calls GetProtocolSettings on the .NET bridge over mTLS and outputs ONE tab-separated
// line to stdout:
//   "min<TAB>max<TAB>signing(0|1)<TAB>encrypt(0|1)<TAB>enabled(0|1)<TAB>wsdd(0|1)<TAB>audit(0|1)"
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
            "/run/secrets/kaimo_bridge_config_sync.crt",
            "KAIMO_BRIDGE_CONFIG_SYNC_KEY",
            "/run/secrets/kaimo_bridge_config_sync.key");
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

    if (reply.min_protocol().empty() || reply.max_protocol().empty()) {
        std::cerr << "kaimo_configsync: incomplete response (empty dialect)." << std::endl;
        return 1;
    }

    std::cout << reply.min_protocol() << '\t' << reply.max_protocol() << '\t'
              << (reply.require_signing() ? '1' : '0') << '\t'
              << (reply.require_encryption() ? '1' : '0') << '\t'
              << (reply.enabled() ? '1' : '0') << '\t'
              << (reply.enable_ws_discovery() ? '1' : '0') << '\t'
              << (reply.enable_audit_log() ? '1' : '0') << '\n';
    std::cerr << "kaimo_configsync: settings received (addr=" << addr << ")." << std::endl;
    return 0;
}
