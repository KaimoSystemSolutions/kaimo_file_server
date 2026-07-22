// kaimo_authd - Authorization and event sidecar for the Samba container.
//
// Bridges Unix socket (from VFS module, pure C) <-> gRPC (to .NET bridge).
// This keeps the smbd VFS module free from gRPC/threads/fork issues.
//
// Protocol (one request per connection, tab-separated, with \n):
//   Authz (response ALLOW|DENY|ERROR):
//     "CONNECT\t<user>\t<share>"
//     "OPEN\t<user>\t<share>\t<access-hex>\t<create 0|1>\t<dir 0|1>\t<listing 0|1>\t<path>"
//       -> "ALLOW\t<granted-access-hex>" | "DENY" | "ERROR"
//     "DELETEAUTH\t<user>\t<share>\t<isdir 0|1>\t<path>"
//   Events (fire-and-forget, response OK):
//     "CLOSE\t<user>\t<share>\t<path>"                   file written and closed
//     "MKDIR\t<user>\t<share>\t<path>"                   directory created
//     "DELETE\t<user>\t<share>\t<isdir 0|1>\t<path>"
//     "RENAME\t<user>\t<share>\t<isdir 0|1>\t<old>\t<new>"
//   Snapshots (Phase 5, @GMT / "Previous Versions"):
//     "SNAPENUM\t<user>\t<share>\t<path>"    -> "OK\t<n>\n<tok1>\n<tok2>..."|"ERROR"
//     "SNAPRESOLVE\t<user>\t<share>\t<@GMT>\t<path>" -> "OK\t<cachepath>\t<size>"|"ERROR"
//
// Each connection is handled in its own thread so slow events
// (versioning reads the file) don't block authorization requests.
#include <chrono>
#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <signal.h>
#include <unistd.h>

#include <grpcpp/grpcpp.h>
#include "kaimo_smb_bridge.grpc.pb.h"

using namespace kaimo::smb::bridge::v1;

static std::string g_bridge_addr;
static std::unique_ptr<AuthzService::Stub> g_authz;
static std::unique_ptr<EventService::Stub> g_events;
static std::unique_ptr<SnapshotService::Stub> g_snapshot;

// --- TTL decision cache (mutex-protected due to multi-threading) ---
struct CacheEntry {
    bool allow;
    uint32_t granted_access;
    std::chrono::steady_clock::time_point expiry;
};
static std::unordered_map<std::string, CacheEntry> g_cache;
static std::mutex g_cache_mtx;
static const auto kCacheTtl = std::chrono::seconds(3);

static bool cache_get(const std::string& key, bool& allow, uint32_t& granted_access) {
    std::lock_guard<std::mutex> lk(g_cache_mtx);
    auto it = g_cache.find(key);
    if (it == g_cache.end()) return false;
    if (std::chrono::steady_clock::now() >= it->second.expiry) { g_cache.erase(it); return false; }
    allow = it->second.allow;
    granted_access = it->second.granted_access;
    return true;
}
static void cache_put(const std::string& key, bool allow, uint32_t granted_access) {
    std::lock_guard<std::mutex> lk(g_cache_mtx);
    g_cache[key] = {
        allow, granted_access, std::chrono::steady_clock::now() + kCacheTtl
    };
}

static std::chrono::system_clock::time_point deadline(int secs) {
    return std::chrono::system_clock::now() + std::chrono::seconds(secs);
}

// ---- Authz ----
static const char* do_connect(const std::string& user, const std::string& share) {
    AuthorizeConnectRequest req; req.set_username(user); req.set_share(share);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeConnect(&ctx, req, &reply);
    if (!st.ok()) { std::cerr << "kaimo_authd: AuthorizeConnect: " << st.error_message() << std::endl; return "ERROR"; }
    return reply.allow() ? "ALLOW" : "DENY";
}

static std::string open_result(bool allow, uint32_t granted_access) {
    if (!allow) return "DENY";
    std::ostringstream out;
    out << "ALLOW\t" << std::hex << std::setw(8) << std::setfill('0')
        << granted_access;
    return out.str();
}

static std::string do_open(const std::string& user, const std::string& share,
                           uint32_t access_mask, bool wants_create,
                           bool create_directory, bool directory_listing,
                           const std::string& path,
                           const std::string& cache_key) {
    bool cached;
    uint32_t cached_access = 0;
    if (cache_get(cache_key, cached, cached_access))
        return open_result(cached, cached_access);
    AuthorizeOpenRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path);
    req.set_access_mask(access_mask);
    req.set_wants_create(wants_create);
    req.set_create_directory(create_directory);
    req.set_directory_listing(directory_listing);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeOpen(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: AuthorizeOpen: " << st.error_message()
                  << std::endl;
        return "ERROR";
    }
    cache_put(cache_key, reply.allow(), reply.granted_access_mask());
    return open_result(reply.allow(), reply.granted_access_mask());
}

static const char* do_delete(const std::string& user, const std::string& share,
                             bool isdir, const std::string& path) {
    AuthorizeDeleteRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path); req.set_is_directory(isdir);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeDelete(&ctx, req, &reply);
    if (!st.ok()) { std::cerr << "kaimo_authd: AuthorizeDelete: " << st.error_message() << std::endl; return "ERROR"; }
    return reply.allow() ? "ALLOW" : "DENY";
}

// ---- Events (best-effort) ----
static void ev_close(const std::string& user, const std::string& share, const std::string& path) {
    NotifyCloseRequest req; req.set_username(user); req.set_share(share); req.set_path(path);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(30));
    NotifyReply reply; g_events->NotifyClose(&ctx, req, &reply);
}
static void ev_mkdir(const std::string& user, const std::string& share, const std::string& path) {
    NotifyPathRequest req; req.set_username(user); req.set_share(share); req.set_path(path); req.set_is_directory(true);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(15));
    NotifyReply reply; g_events->NotifyMkdir(&ctx, req, &reply);
}
static void ev_delete(const std::string& user, const std::string& share, bool isdir, const std::string& path) {
    NotifyPathRequest req; req.set_username(user); req.set_share(share); req.set_path(path); req.set_is_directory(isdir);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(15));
    NotifyReply reply; g_events->NotifyDelete(&ctx, req, &reply);
}
static void ev_rename(const std::string& user, const std::string& share, bool isdir,
                      const std::string& oldp, const std::string& newp) {
    NotifyRenameRequest req; req.set_username(user); req.set_share(share);
    req.set_old_path(oldp); req.set_new_path(newp); req.set_is_directory(isdir);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(15));
    NotifyReply reply; g_events->NotifyRename(&ctx, req, &reply);
}

// ---- Snapshots (Phase 5, @GMT / "Previous Versions") ----
static std::string do_snapenum(const std::string& user, const std::string& share,
                               const std::string& path) {
    EnumerateSnapshotsRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(10));
    EnumerateSnapshotsReply reply;
    grpc::Status st = g_snapshot->EnumerateSnapshots(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: EnumerateSnapshots: " << st.error_message() << std::endl;
        return "ERROR\n";
    }
    std::string out = "OK\t" + std::to_string(reply.gmt_tokens_size()) + "\n";
    for (const auto& t : reply.gmt_tokens()) out += t + "\n";
    return out;
}

static std::string do_snapresolve(const std::string& user, const std::string& share,
                                  const std::string& token, const std::string& path) {
    ResolveVersionRequest req;
    req.set_username(user); req.set_share(share);
    req.set_gmt_token(token); req.set_path(path);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(30));
    ResolveVersionReply reply;
    grpc::Status st = g_snapshot->ResolveVersion(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: ResolveVersion: " << st.error_message() << std::endl;
        return "ERROR\n";
    }
    if (!reply.found()) return "ERROR\n";
    return "OK\t" + reply.cache_path() + "\t" + std::to_string(reply.size()) + "\n";
}

// Splits into up to max fields; the last field takes the rest.
static std::vector<std::string> split_tabs(const std::string& line, size_t max_fields) {
    std::vector<std::string> parts;
    size_t start = 0;
    while (parts.size() + 1 < max_fields) {
        auto tab = line.find('\t', start);
        if (tab == std::string::npos) break;
        parts.push_back(line.substr(start, tab - start));
        start = tab + 1;
    }
    parts.push_back(line.substr(start));
    return parts;
}

static bool parse_hex_u32(const std::string& text, uint32_t& value) {
    if (text.size() != 8) return false;
    char* end = nullptr;
    errno = 0;
    unsigned long parsed = std::strtoul(text.c_str(), &end, 16);
    if (errno == ERANGE || end == text.c_str() || *end != '\0' ||
        parsed > std::numeric_limits<uint32_t>::max())
        return false;
    value = static_cast<uint32_t>(parsed);
    return true;
}

static void handle_client(int cfd) {
    char buf[8192];
    ssize_t r = read(cfd, buf, sizeof(buf) - 1);
    if (r <= 0) { close(cfd); return; }
    buf[r] = '\0';
    std::string line(buf);
    auto nl = line.find('\n');
    if (nl != std::string::npos) line.resize(nl);

    std::string out = "ERROR\n"; // default; snapshot handlers set a full multi-line reply
    if (line.rfind("CONNECT\t", 0) == 0) {
        auto p = split_tabs(line, 3);
        if (p.size() == 3) out = std::string(do_connect(p[1], p[2])) + "\n";
    } else if (line.rfind("OPEN\t", 0) == 0) {
        auto p = split_tabs(line, 8);
        uint32_t access_mask = 0;
        if (p.size() == 8 && parse_hex_u32(p[3], access_mask) &&
            (p[4] == "0" || p[4] == "1") &&
            (p[5] == "0" || p[5] == "1") &&
            (p[6] == "0" || p[6] == "1")) {
            out = do_open(p[1], p[2], access_mask,
                          p[4] == "1", p[5] == "1", p[6] == "1",
                          p[7], line) + "\n";
        }
    } else if (line.rfind("DELETEAUTH\t", 0) == 0) {
        auto p = split_tabs(line, 5);
        if (p.size() == 5) out = std::string(do_delete(p[1], p[2], p[3] == "1", p[4])) + "\n";
    } else if (line.rfind("CLOSE\t", 0) == 0) {
        auto p = split_tabs(line, 4);
        if (p.size() == 4) { ev_close(p[1], p[2], p[3]); out = "OK\n"; }
    } else if (line.rfind("MKDIR\t", 0) == 0) {
        auto p = split_tabs(line, 4);
        if (p.size() == 4) { ev_mkdir(p[1], p[2], p[3]); out = "OK\n"; }
    } else if (line.rfind("DELETE\t", 0) == 0) {
        auto p = split_tabs(line, 5);
        if (p.size() == 5) { ev_delete(p[1], p[2], p[3] == "1", p[4]); out = "OK\n"; }
    } else if (line.rfind("RENAME\t", 0) == 0) {
        auto p = split_tabs(line, 6);
        if (p.size() == 6) { ev_rename(p[1], p[2], p[3] == "1", p[4], p[5]); out = "OK\n"; }
    } else if (line.rfind("SNAPENUM\t", 0) == 0) {
        auto p = split_tabs(line, 4);
        if (p.size() == 4) out = do_snapenum(p[1], p[2], p[3]);
    } else if (line.rfind("SNAPRESOLVE\t", 0) == 0) {
        auto p = split_tabs(line, 5);
        if (p.size() == 5) out = do_snapresolve(p[1], p[2], p[3], p[4]);
    } else {
        std::cerr << "kaimo_authd: unknown request: " << line << std::endl;
    }

    (void)write(cfd, out.c_str(), out.size());
    close(cfd);
}

int main() {
    signal(SIGPIPE, SIG_IGN);

    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    g_bridge_addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";
    const char* sock_env = std::getenv("KAIMO_AUTHD_SOCK");
    std::string sock_path = sock_env ? sock_env : "/var/run/kaimo/authz.sock";

    auto channel = grpc::CreateChannel(g_bridge_addr, grpc::InsecureChannelCredentials());
    g_authz = AuthzService::NewStub(channel);
    g_events = EventService::NewStub(channel);
    g_snapshot = SnapshotService::NewStub(channel);

    int sfd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (sfd < 0) { std::cerr << "kaimo_authd: socket() failed" << std::endl; return 1; }

    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, sock_path.c_str(), sizeof(addr.sun_path) - 1);
    unlink(sock_path.c_str());

    if (bind(sfd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        std::cerr << "kaimo_authd: bind(" << sock_path << ") failed" << std::endl; return 1;
    }
    chmod(sock_path.c_str(), 0666);
    if (listen(sfd, 128) != 0) { std::cerr << "kaimo_authd: listen() failed" << std::endl; return 1; }

    std::cerr << "kaimo_authd: ready. socket=" << sock_path << " bridge=" << g_bridge_addr << std::endl;

    for (;;) {
        int cfd = accept(sfd, nullptr, nullptr);
        if (cfd < 0) continue;
        std::thread(handle_client, cfd).detach();
    }
    return 0;
}
