// kaimo_authsync - gRPC C++ client for NT-Hash sync.
//
// Calls ListUsers on the .NET bridge over mTLS and emits one versioned JSON
// document. The import into Samba's
// tdbsam is handled by the shell script sync-users.sh.
//
// Also proves that gRPC works from the C/C++ environment of the Samba container
// (last toolchain risk from Phase 0).
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>
#include <utility>
#include <vector>

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

    constexpr std::uint32_t page_size = 1000;
    constexpr std::uint32_t maximum_users = 100000;
    std::uint32_t offset = 0;
    std::uint32_t rejected_users = 0;
    std::string continuation_token;
    std::size_t estimated_json_bytes = 32;
    std::vector<std::pair<std::string, std::string>> users;
    users.reserve(page_size);

    for (;;) {
        grpc::ClientContext ctx;
        ctx.set_deadline(
            std::chrono::system_clock::now() + std::chrono::seconds(10));
        ListUsersRequest req;
        req.set_offset(offset);
        req.set_page_size(page_size);
        req.set_continuation_token(continuation_token);
        ListUsersReply reply;
        grpc::Status status = stub->ListUsers(&ctx, req, &reply);
        if (!status.ok()) {
            std::cerr << "kaimo_authsync: ListUsers RPC failed: "
                      << status.error_code() << " " << status.error_message()
                      << " (addr=" << addr << ", offset=" << offset << ")"
                      << std::endl;
            return 1;
        }

        if (reply.users_size() > static_cast<int>(page_size)
            || reply.next_offset() < offset
            || (reply.has_more() && reply.next_offset() <= offset)
            || reply.next_offset() > maximum_users
            || reply.rejected_users() > maximum_users
            || rejected_users > maximum_users - reply.rejected_users()
            || static_cast<std::uint64_t>(reply.users_size())
                    + reply.rejected_users()
                > static_cast<std::uint64_t>(reply.next_offset() - offset)
            || (reply.has_more() && reply.continuation_token().empty())
            || (!reply.has_more() && !reply.continuation_token().empty())) {
            std::cerr << "kaimo_authsync: invalid pagination metadata rejected."
                      << std::endl;
            return 1;
        }
        rejected_users += reply.rejected_users();

        for (const auto& user : reply.users()) {
            const std::string& hash = user.nt_hash();
            if (!kaimo::sync_json::valid_username(user.username())
                || hash.size() != 16) {
                std::cerr << "kaimo_authsync: invalid user record rejected."
                          << std::endl;
                return 1;
            }
            estimated_json_bytes += user.username().size() + 80;
            if (users.size() >= maximum_users
                || estimated_json_bytes > 16 * 1024 * 1024) {
                std::cerr
                    << "kaimo_authsync: structured response exceeds safety limit."
                    << std::endl;
                return 1;
            }
            users.emplace_back(user.username(), hash);
        }

        if (!reply.has_more()) break;
        offset = reply.next_offset();
        continuation_token = reply.continuation_token();
    }

    std::cout << "{\"version\":1,\"users\":[";
    bool first = true;
    for (const auto& user : users) {
        if (!first) std::cout << ',';
        first = false;
        std::cout << "{\"username\":"
                  << kaimo::sync_json::quote(user.first)
                  << ",\"nt_hash\":"
                  << kaimo::sync_json::quote(to_hex(user.second)) << '}';
    }
    std::cout << "]}\n";
    std::cerr << "kaimo_authsync: " << users.size()
              << " users received in bounded pages; " << rejected_users
              << " invalid credential rows skipped (addr=" << addr << ")."
              << std::endl;
    return 0;
}
