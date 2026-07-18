// kaimo_authsync - gRPC-C++-Client fuer den NT-Hash-Sync.
//
// Ruft ListUsers auf der .NET-Bridge (h2c) auf und gibt je aktivem Benutzer
// eine Zeile "username<TAB>NTHASHHEX" auf stdout aus. Der Import in Sambas
// tdbsam macht das Shell-Skript sync-users.sh.
//
// Beweist zugleich, dass gRPC aus dem C/C++-Umfeld des Samba-Containers
// funktioniert (letztes Toolchain-Risiko aus Phase 0).
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>
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

    auto channel = grpc::CreateChannel(addr, grpc::InsecureChannelCredentials());
    std::unique_ptr<AuthService::Stub> stub = AuthService::NewStub(channel);

    grpc::ClientContext ctx;
    ctx.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));

    ListUsersRequest req;
    ListUsersReply reply;
    grpc::Status status = stub->ListUsers(&ctx, req, &reply);
    if (!status.ok()) {
        std::cerr << "kaimo_authsync: ListUsers RPC fehlgeschlagen: "
                  << status.error_code() << " " << status.error_message()
                  << " (addr=" << addr << ")" << std::endl;
        return 1;
    }

    for (const auto& u : reply.users()) {
        const std::string& h = u.nt_hash();
        if (h.size() != 16) continue;  // nur gueltige 16-Byte-NT-Hashes
        std::cout << u.username() << '\t' << to_hex(h) << '\n';
    }
    std::cerr << "kaimo_authsync: " << reply.users_size()
              << " Benutzer empfangen (addr=" << addr << ")." << std::endl;
    return 0;
}
