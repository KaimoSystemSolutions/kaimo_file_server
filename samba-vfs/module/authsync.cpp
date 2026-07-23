// kaimo_authsync - gRPC C++ client for NT-Hash sync.
//
// Calls ListUsers on the .NET bridge over mTLS and outputs one line
// "username<TAB>NTHASHHEX" per active user to stdout. The import into Samba's
// tdbsam is handled by the shell script sync-users.sh.
//
// Also proves that gRPC works from the C/C++ environment of the Samba container
// (last toolchain risk from Phase 0).
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "kaimo_smb_bridge.grpc.pb.h"

using kaimo::smb::bridge::v1::AuthService;
using kaimo::smb::bridge::v1::ListUsersReply;
using kaimo::smb::bridge::v1::ListUsersRequest;

static std::string to_hex(const std::string& bytes) {
    static const char* digits = "0123456789ABCDEF";
    std::string out;
    out.reserve(bytes.size() * 2);
    for (unsigned char c : bytes) {
        out.push_back(digits[c >> 4]);
        out.push_back(digits[c & 0x0F]);
    }
    return out;
}

int main() {
    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";

    std::shared_ptr<grpc::Channel> channel;
    try {
        channel = kaimo::control_plane::create_mtls_channel(
            addr,
            "KAIMO_BRIDGE_AUTH_SYNC_CERT",
            "/run/secrets/kaimo-control-plane/auth-sync.crt",
            "KAIMO_BRIDGE_AUTH_SYNC_KEY",
            "/run/secrets/kaimo-control-plane/auth-sync.key");
    } catch (const std::exception& error) {
        std::cerr << "kaimo_authsync: " << error.what() << std::endl;
        return 1;
    }
    std::unique_ptr<AuthService::Stub> stub = AuthService::NewStub(channel);

    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));

    ListUsersRequest req;
    ListUsersReply reply;
    grpc::Status status = stub->ListUsers(&ctx, req, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_authsync: ListUsers RPC failed: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return 1;
    }

    for (const auto& u : reply.users()) {
        const std::string& h = u.nt_hash();
        if (h.size() != 16) continue;  // only valid 16-byte NT-Hashes
        std::cout << u.username() << '\t' << to_hex(h) << '\n';
    }
    std::cerr << "kaimo_authsync: " << reply.users_size()
              << " users received (addr=" << addr << ")." << std::endl;
    return 0;
}
