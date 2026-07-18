// kaimo_configsync - gRPC C++ client for protocol settings (Phase 4).
//
// Calls GetProtocolSettings on the .NET bridge (h2c) and outputs ONE line
// "min<TAB>max<TAB>signing(0|1)<TAB>encrypt(0|1)" to stdout. The mapping
// to Samba's global parameters (net conf setparm global) is handled by the
// shell script sync-config.sh. Mirror to kaimo_authsync / kaimo_sharesync.
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
#include "kaimo_smb_bridge.grpc.pb.h"

using kaimo::smb::bridge::v1::ConfigService;
using kaimo::smb::bridge::v1::GetProtocolSettingsRequest;
using kaimo::smb::bridge::v1::ProtocolSettingsReply;

int main() {
    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";

    auto channel = grpc::CreateChannel(addr, grpc::InsecureChannelCredentials());
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
              << (reply.require_encryption() ? '1' : '0') << '\n';
    std::cerr << "kaimo_configsync: settings received (addr=" << addr << ")." << std::endl;
    return 0;
}
