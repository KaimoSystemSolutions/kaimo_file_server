// kaimo_authd - Authorization and event sidecar for the Samba container.
//
// Bridges Unix socket (from VFS module, pure C) <-> gRPC (to .NET bridge).
// This keeps the smbd VFS module free from gRPC/threads/fork issues.
//
// Protocol (one request per connection, tab-separated, with \n):
//   Authz (response ALLOW|DENY|ERROR):
//     "CONNECT\t<user>\t<share>"
//     "OPEN\t<user>\t<share>\t<flags>\t<path>"          flags: r/w/c
//   Events (fire-and-forget, response OK):
//     "CLOSE\t<user>\t<share>\t<path>"                   file written and closed
//     "MKDIR\t<user>\t<share>\t<path>"                   directory created
//     "DELETE\t<user>\t<share>\t<isdir 0|1>\t<path>"
//     "RENAME\t<user>\t<share>\t<isdir 0|1>\t<old>\t<new>"
//
// Each connection is handled in its own thread so slow events
// (versioning reads the file) don't block authorization requests.
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <memory>
#include <mutex>
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

// --- TTL decision cache (mutex-protected due to multi-threading) ---
struct CacheEntry { bool allow; std::chrono::steady_clock::time_point expiry; };
static std::unordered_map<std::string, CacheEntry> g_cache;
static std::mutex g_cache_mtx;
static const auto kCacheTtl = std::chrono::seconds(3);

static bool cache_get(const std::string& key, bool& allow) {
    std::lock_guard<std::mutex> lk(g_cache_mtx);
    auto it = g_cache.find(key);
    if (it == g_cache.end()) return false;
    if (std::chrono::steady_clock::now() >= it->second.expiry) { g_cache.erase(it); return false; }
    allow = it->second.allow;
    return true;
}
static void cache_put(const std::string& key, bool allow) {
    std::lock_guard<std::mutex> lk(g_cache_mtx);
    g_cache[key] = { allow, std::chrono::steady_clock::now() + kCacheTtl };
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

static const char* do_open(const std::string& user, const std::string& share,
                           const std::string& flags, const std::string& path,
                           const std::string& cache_key) {
    bool cached;
    if (cache_get(cache_key, cached)) return cached ? "ALLOW" : "DENY";
    AuthorizeOpenRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path);
    req.set_want_read(flags.find('r') != std::string::npos);
    req.set_want_write(flags.find('w') != std::string::npos);
    req.set_wants_create(flags.find('c') != std::string::npos);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeOpen(&ctx, req, &reply);
    if (!st.ok()) { std::cerr << "kaimo_authd: AuthorizeOpen: " << st.error_message() << std::endl; return "ERROR"; }
    cache_put(cache_key, reply.allow());
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

static void handle_client(int cfd) {
    char buf[8192];
    ssize_t r = read(cfd, buf, sizeof(buf) - 1);
    if (r <= 0) { close(cfd); return; }
    buf[r] = '\0';
    std::string line(buf);
    auto nl = line.find('\n');
    if (nl != std::string::npos) line.resize(nl);

    const char* result = "ERROR";
    if (line.rfind("CONNECT\t", 0) == 0) {
        auto p = split_tabs(line, 3);
        if (p.size() == 3) result = do_connect(p[1], p[2]);
    } else if (line.rfind("OPEN\t", 0) == 0) {
        auto p = split_tabs(line, 5);
        if (p.size() == 5) result = do_open(p[1], p[2], p[3], p[4], line);
    } else if (line.rfind("CLOSE\t", 0) == 0) {
        auto p = split_tabs(line, 4);
        if (p.size() == 4) { ev_close(p[1], p[2], p[3]); result = "OK"; }
    } else if (line.rfind("MKDIR\t", 0) == 0) {
        auto p = split_tabs(line, 4);
        if (p.size() == 4) { ev_mkdir(p[1], p[2], p[3]); result = "OK"; }
    } else if (line.rfind("DELETE\t", 0) == 0) {
        auto p = split_tabs(line, 5);
        if (p.size() == 5) { ev_delete(p[1], p[2], p[3] == "1", p[4]); result = "OK"; }
    } else if (line.rfind("RENAME\t", 0) == 0) {
        auto p = split_tabs(line, 6);
        if (p.size() == 6) { ev_rename(p[1], p[2], p[3] == "1", p[4], p[5]); result = "OK"; }
    } else {
        std::cerr << "kaimo_authd: unknown request: " << line << std::endl;
    }

    std::string out = std::string(result) + "\n";
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
