// kaimo_sharesync - gRPC C++ client for share provisioning (Phase 4).
//
// Calls ListShares on the .NET bridge over mTLS and emits one versioned JSON
// document. The actual
// reconciliation in Samba's registry (net conf addshare/setparm/delshare) is
// handled by the shell script sync-shares.sh. Mirror to kaimo_authsync (NT-Hash sync).
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "kaimo_smb_bridge.grpc.pb.h"
#include "sync_json.h"

using kaimo::smb::bridge::v1::ShareService;
using kaimo::smb::bridge::v1::ListSharesReply;
using kaimo::smb::bridge::v1::ListSharesRequest;

int main() {
    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";

    std::shared_ptr<grpc::Channel> channel;
    try {
        channel = kaimo::control_plane::create_mtls_channel(
            addr,
            "KAIMO_BRIDGE_SHARE_SYNC_CERT",
            "/run/secrets/kaimo-control-plane/samba.crt",
            "KAIMO_BRIDGE_SHARE_SYNC_KEY",
            "/run/secrets/kaimo-control-plane/samba.key");
    } catch (const std::exception& error) {
        std::cerr << "kaimo_sharesync: " << error.what() << std::endl;
        return 1;
    }
    std::unique_ptr<ShareService::Stub> stub = ShareService::NewStub(channel);

    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));

    ListSharesRequest req;
    ListSharesReply reply;
    grpc::Status status = stub->ListShares(&ctx, req, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_sharesync: ListShares RPC failed: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return 1;
    }

    if (reply.shares_size() > 100000) {
        std::cerr << "kaimo_sharesync: response exceeds share limit." << std::endl;
        return 1;
    }

    std::size_t estimated_json_bytes = 32;
    for (const auto& s : reply.shares()) {
        if (!kaimo::sync_json::valid_share_name(s.name())
            || !kaimo::sync_json::valid_absolute_path(s.path())) {
            std::cerr << "kaimo_sharesync: invalid share record rejected." << std::endl;
            return 1;
        }
        estimated_json_bytes += s.name().size() + s.path().size() + 64;
        if (estimated_json_bytes > 16 * 1024 * 1024) {
            std::cerr << "kaimo_sharesync: JSON response exceeds size limit." << std::endl;
            return 1;
        }
    }

    std::cout << "{\"version\":1,\"shares\":[";
    bool first = true;
    for (const auto& s : reply.shares()) {
        if (!first) std::cout << ',';
        first = false;
        std::cout << "{\"name\":" << kaimo::sync_json::quote(s.name())
                  << ",\"path\":" << kaimo::sync_json::quote(s.path())
                  << ",\"hidden\":" << kaimo::sync_json::boolean(s.is_hidden())
                  << '}';
    }
    std::cout << "]}\n";
    std::cerr << "kaimo_sharesync: " << reply.shares_size()
              << " shares received (addr=" << addr << ")." << std::endl;
    return 0;
}
