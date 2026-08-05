// kaimo_authsync - gRPC C++ client for NT-Hash sync.
//
// Calls ListUsers on the .NET bridge over mTLS and emits one versioned JSON
// document. The import into Samba's
// tdbsam is handled by the shell script sync-users.sh.
//
// Also proves that gRPC works from the C/C++ environment of the Samba container
// (last toolchain risk from Phase 0).
#include <algorithm>
#include <array>
#include <cerrno>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "kaimo_smb_bridge.grpc.pb.h"
#include "sync_json.h"

using kaimo::smb::bridge::v1::AuthService;
using kaimo::smb::bridge::v1::ListUsersReply;
using kaimo::smb::bridge::v1::ListUsersRequest;

static void secure_zero(void* data, std::size_t size) noexcept {
    volatile unsigned char* current =
        static_cast<volatile unsigned char*>(data);
    while (size-- > 0) {
        *current++ = 0;
    }
}

struct credential {
    std::string username;
    std::array<unsigned char, 16> nt_hash{};

    credential(std::string name, const std::string& hash)
        : username(std::move(name)) {
        std::copy(hash.begin(), hash.end(), nt_hash.begin());
    }

    credential(const credential&) = delete;
    credential& operator=(const credential&) = delete;

    credential(credential&& other) noexcept
        : username(std::move(other.username)), nt_hash(other.nt_hash) {
        secure_zero(other.nt_hash.data(), other.nt_hash.size());
    }

    credential& operator=(credential&& other) noexcept {
        if (this != &other) {
            secure_zero(nt_hash.data(), nt_hash.size());
            username = std::move(other.username);
            nt_hash = other.nt_hash;
            secure_zero(other.nt_hash.data(), other.nt_hash.size());
        }
        return *this;
    }

    ~credential() {
        secure_zero(nt_hash.data(), nt_hash.size());
    }
};

static void write_hex(std::ostream& output,
                      const std::array<unsigned char, 16>& bytes) {
    static constexpr char digits[] = "0123456789ABCDEF";
    output.put('"');
    for (unsigned char value : bytes) {
        output.put(digits[value >> 4]);
        output.put(digits[value & 0x0f]);
    }
    output.put('"');
}

static void clear_reply_hashes(ListUsersReply& reply) {
    for (auto& user : *reply.mutable_users()) {
        user.clear_nt_hash();
    }
}

static bool read_retry_after(const grpc::ClientContext& context,
                             std::chrono::milliseconds& delay) {
    const auto& trailers = context.GetServerTrailingMetadata();
    auto value = trailers.find("retry-after-ms");
    if (value == trailers.end()) return false;

    std::string encoded(value->second.data(), value->second.length());
    if (encoded.empty()) return false;
    errno = 0;
    char* end = nullptr;
    unsigned long parsed = std::strtoul(encoded.c_str(), &end, 10);
    if (errno != 0 || end == encoded.c_str() || *end != '\0'
        || parsed > 300000UL) {
        return false;
    }

    // Cross the fixed-window boundary rather than retrying on its last
    // millisecond and being rejected again due to clock/scheduling jitter.
    delay = std::chrono::milliseconds(parsed + 250UL);
    return true;
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
    std::vector<credential> users;
    users.reserve(page_size);
    bool rate_limit_retry_used = false;

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
            std::chrono::milliseconds retry_delay;
            if (offset == 0
                && !rate_limit_retry_used
                && status.error_code()
                    == grpc::StatusCode::RESOURCE_EXHAUSTED
                && read_retry_after(ctx, retry_delay)) {
                clear_reply_hashes(reply);
                rate_limit_retry_used = true;
                std::cerr << "kaimo_authsync: NT-hash export rate limited; "
                          << "retrying after " << retry_delay.count()
                          << " ms." << std::endl;
                std::this_thread::sleep_for(retry_delay);
                continue;
            }
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
            clear_reply_hashes(reply);
            std::cerr << "kaimo_authsync: invalid pagination metadata rejected."
                      << std::endl;
            return 1;
        }
        rejected_users += reply.rejected_users();

        for (auto& user : *reply.mutable_users()) {
            const std::string& hash = user.nt_hash();
            if (!kaimo::sync_json::valid_username(user.username())
                || hash.size() != 16) {
                clear_reply_hashes(reply);
                std::cerr << "kaimo_authsync: invalid user record rejected."
                          << std::endl;
                return 1;
            }
            estimated_json_bytes += user.username().size() + 80;
            if (users.size() >= maximum_users
                || estimated_json_bytes > 16 * 1024 * 1024) {
                clear_reply_hashes(reply);
                std::cerr
                    << "kaimo_authsync: structured response exceeds safety limit."
                    << std::endl;
                return 1;
            }
            users.emplace_back(user.username(), hash);
            // The move-only credential owns the only application-level copy
            // retained across pages. Release protobuf's mutable copy now.
            user.clear_nt_hash();
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
                  << kaimo::sync_json::quote(user.username)
                  << ",\"nt_hash\":";
        write_hex(std::cout, user.nt_hash);
        std::cout << '}';
    }
    std::cout << "]}\n";
    std::cout.flush();
    std::cerr << "kaimo_authsync: " << users.size()
              << " users received in bounded pages; " << rejected_users
              << " invalid credential rows skipped (addr=" << addr << ")."
              << std::endl;
    return 0;
}
