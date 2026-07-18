// kaimo_sharesync - gRPC-C++-Client fuer das Share-Provisioning (Phase 4).
//
// Ruft ListShares auf der .NET-Bridge (h2c) auf und gibt je aktiviertem Share
// eine Zeile "name<TAB>path<TAB>hidden(0|1)" auf stdout aus. Die eigentliche
// Reconciliation in Sambas Registry (net conf addshare/setparm/delshare) macht
// das Shell-Skript sync-shares.sh. Spiegelbild zu kaimo_authsync (NT-Hash-Sync).
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
#include "kaimo_smb_bridge.grpc.pb.h"

using kaimo::smb::bridge::v1::ShareService;
using kaimo::smb::bridge::v1::ListSharesReply;
using kaimo::smb::bridge::v1::ListSharesRequest;

int main() {
    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";

    auto channel = grpc::CreateChannel(addr, grpc::InsecureChannelCredentials());
    std::unique_ptr<ShareService::Stub> stub = ShareService::NewStub(channel);

    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));

    ListSharesRequest req;
    ListSharesReply reply;
    grpc::Status status = stub->ListShares(&ctx, req, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_sharesync: ListShares RPC fehlgeschlagen: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return 1;
    }

    for (const auto& s : reply.shares()) {
        if (s.name().empty() || s.path().empty()) continue;  // unvollstaendige Eintraege ueberspringen
        std::cout << s.name() << '\t' << s.path() << '\t'
                  << (s.is_hidden() ? '1' : '0') << '\n';
    }
    std::cerr << "kaimo_sharesync: " << reply.shares_size()
              << " Shares empfangen (addr=" << addr << ")." << std::endl;
    return 0;
}
