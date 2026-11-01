// kaimo_authreport - gRPC C++ client for SMB login events (AP8d).
//
// kaimo-samba-log-forwarder.py spools Samba's auth_json_audit events as
// private TSV batches (auth-<ns>-<seq>.tsv: unix ms, account, address, NT
// status). This client sends every complete batch, oldest first, to
// SecurityEventService/ReportAuthentication over mTLS and deletes a batch only
// after the bridge acknowledged all of its events. A failed call keeps the
// batch for the next run (entrypoint loop); the events are informational, so
// a failure never touches Samba's state. Mirror to kaimo_configsync.
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <regex>
#include <string>
#include <vector>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "kaimo_smb_bridge.grpc.pb.h"
#include "sync_json.h"

using kaimo::smb::bridge::v1::AuthenticationEvent;
using kaimo::smb::bridge::v1::ReportAuthenticationReply;
using kaimo::smb::bridge::v1::ReportAuthenticationRequest;
using kaimo::smb::bridge::v1::SecurityEventService;
namespace fs = std::filesystem;

namespace {

constexpr int kMaxEventsPerCall = 1000;           // SecurityEventGrpcService.MaxEventsPerCall
constexpr std::uintmax_t kMaxBatchBytes = 4 << 20;  // forwarder batches stay far below this

// One TSV record; false for anything the forwarder would never write.
bool parse_record(const std::string& line, AuthenticationEvent& event) {
    static const std::regex record(
        // Account: up to 256 characters from the forwarder, i.e. at most 1024 UTF-8 bytes.
        "^([0-9]{1,19})\t([^\t]{1,1024})\t([0-9A-Fa-f.:]{0,64})\t(NT_STATUS_[A-Z0-9_]{1,64})$");
    std::smatch match;
    if (!std::regex_match(line, match, record)) return false;
    if (!kaimo::sync_json::valid_text(match[2].str(), 1024)) return false;
    event.set_at_unix_ms(std::stoll(match[1].str()));
    event.set_username(match[2].str());
    event.set_address(match[3].str());
    event.set_status(match[4].str());
    return true;
}

// Complete forwarder batches only: regular files named auth-*.tsv (no links, no .tmp).
std::vector<fs::path> pending_batches(const fs::path& spool) {
    static const std::regex name("^auth-[0-9]{20}-[0-9]{6}\\.tsv$");
    std::vector<fs::path> batches;
    std::error_code error;
    for (const auto& entry : fs::directory_iterator(spool, error)) {
        if (entry.is_symlink() || !entry.is_regular_file()) continue;
        if (std::regex_match(entry.path().filename().string(), name))
            batches.push_back(entry.path());
    }
    std::sort(batches.begin(), batches.end());
    return batches;
}

bool send(SecurityEventService::Stub& stub, const ReportAuthenticationRequest& request,
          const std::string& addr) {
    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));
    ReportAuthenticationReply reply;
    grpc::Status status = stub.ReportAuthentication(&ctx, request, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_authreport: ReportAuthentication RPC failed: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return false;
    }
    return true;
}

}  // namespace

int main() {
    const char* spool_env = std::getenv("KAIMO_AUTH_EVENT_SPOOL");
    const fs::path spool = spool_env && spool_env[0] ? spool_env : "/var/run/kaimo/auth-events";
    std::vector<fs::path> batches = pending_batches(spool);
    if (batches.empty()) return 0;

    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    std::string addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";
    std::shared_ptr<grpc::Channel> channel;
    try {
        channel = kaimo::control_plane::create_mtls_channel(
            addr,
            "KAIMO_BRIDGE_AUTH_REPORT_CERT",
            "/run/secrets/kaimo-control-plane/samba.crt",
            "KAIMO_BRIDGE_AUTH_REPORT_KEY",
            "/run/secrets/kaimo-control-plane/samba.key");
    } catch (const std::exception& error) {
        std::cerr << "kaimo_authreport: " << error.what() << std::endl;
        return 1;
    }
    std::unique_ptr<SecurityEventService::Stub> stub = SecurityEventService::NewStub(channel);

    for (const fs::path& path : batches) {
        std::error_code error;
        if (fs::file_size(path, error) > kMaxBatchBytes || error) {
            std::cerr << "kaimo_authreport: discarding unreadable or oversized batch "
                      << path.filename() << std::endl;
            fs::remove(path, error);
            continue;
        }
        std::ifstream input(path);
        ReportAuthenticationRequest request;
        int skipped = 0;
        for (std::string line; std::getline(input, line);) {
            if (!parse_record(line, *request.add_events())) {
                request.mutable_events()->RemoveLast();
                ++skipped;
                continue;
            }
            if (request.events_size() == kMaxEventsPerCall) {
                if (!send(*stub, request, addr)) return 1;
                request.clear_events();
            }
        }
        if (request.events_size() > 0 && !send(*stub, request, addr)) return 1;
        if (skipped > 0)
            std::cerr << "kaimo_authreport: skipped " << skipped << " malformed record(s) in "
                      << path.filename() << std::endl;
        // ponytail: a failure or crash after a sent chunk re-sends that chunk with the batch
        // (an over-count in the overview, never a loss).
        fs::remove(path, error);
    }
    return 0;
}
