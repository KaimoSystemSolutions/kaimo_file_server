// kaimo_authsync - gRPC C++ client for NT-Hash sync.
//
// Calls ListUsers on the .NET bridge over mTLS and emits one versioned JSON
// document. The import into Samba's
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
#include "sync_json.h"

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
            "/run/secrets/kaimo-control-plane/samba.crt",
            "KAIMO_BRIDGE_AUTH_SYNC_KEY",
            "/run/secrets/kaimo-control-plane/samba.key");
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

    if (reply.users_size() > 100000) {
        std::cerr << "kaimo_authsync: response exceeds user limit." << std::endl;
        return 1;
    }

    std::size_t estimated_json_bytes = 32;
    for (const auto& u : reply.users()) {
        const std::string& h = u.nt_hash();
        if (!kaimo::sync_json::valid_username(u.username()) || h.size() != 16) {
            std::cerr << "kaimo_authsync: invalid user record rejected." << std::endl;
            return 1;
        }
        estimated_json_bytes += u.username().size() + 80;
        if (estimated_json_bytes > 16 * 1024 * 1024) {
            std::cerr << "kaimo_authsync: JSON response exceeds size limit." << std::endl;
            return 1;
        }
    }

    std::cout << "{\"version\":1,\"users\":[";
    bool first = true;
    for (const auto& u : reply.users()) {
        if (!first) std::cout << ',';
        first = false;
        std::cout << "{\"username\":" << kaimo::sync_json::quote(u.username())
                  << ",\"nt_hash\":" << kaimo::sync_json::quote(to_hex(u.nt_hash())) << '}';
    }
    std::cout << "]}\n";
    std::cerr << "kaimo_authsync: " << reply.users_size()
              << " users received (addr=" << addr << ")." << std::endl;
    return 0;
}
