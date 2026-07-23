// kaimo_authd - Authorization and event sidecar for the Samba container.
//
// Bridges Unix socket (from VFS module, pure C) <-> gRPC (to .NET bridge).
// This keeps the smbd VFS module free from gRPC/threads/fork issues.
//
// The local protocol is a versioned, length-prefixed binary envelope. Every
// request and response has an explicit operation enum, status, and bounded
// payload. Variable strings are individually length-prefixed, so path or
// identity bytes cannot alter field boundaries.
//
// Connections are handled by a fixed-size worker pool. Accepted descriptors
// wait in a bounded queue; excess clients receive ERROR and are closed. Socket
// receive/send deadlines ensure a client that sends nothing cannot pin a worker
// indefinitely.
#include <atomic>
#include <chrono>
#include <cerrno>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <iostream>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <grp.h>
#include <fcntl.h>
#include <limits.h>
#include <pwd.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <signal.h>
#include <unistd.h>

#include <grpcpp/grpcpp.h>
#include "bridge_channel.h"
#include "decision_cache.h"
#include "kaimo_smb_bridge.grpc.pb.h"
#include "local_protocol.h"

using namespace kaimo::smb::bridge::v1;

static std::string g_bridge_addr;
static std::unique_ptr<AuthzService::Stub> g_authz;
static std::unique_ptr<EventService::Stub> g_events;
static std::unique_ptr<SnapshotService::Stub> g_snapshot;
static std::unique_ptr<kaimo::authd::DecisionCache> g_cache;
static std::atomic<uint64_t> g_overload_rejections{0};
static std::atomic<uint64_t> g_receive_timeouts{0};
static std::atomic<uint64_t> g_cache_evictions{0};
static std::atomic<uint64_t> g_cache_oversize_skips{0};
static std::atomic<uint64_t> g_cache_requests{0};
static std::atomic<uint64_t> g_peer_rejections{0};
static dev_t g_expected_peer_device;
static ino_t g_expected_peer_inode;
static std::string g_expected_peer_name;

struct PeerIdentity {
    pid_t pid;
    uid_t uid;
    gid_t gid;
};

struct ClientConnection {
    int fd;
    PeerIdentity peer;
};

class BoundedClientQueue {
public:
    explicit BoundedClientQueue(size_t capacity) : capacity_(capacity) {}

    bool try_push(const ClientConnection& client) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (clients_.size() >= capacity_) return false;
        clients_.push_back(client);
        ready_.notify_one();
        return true;
    }

    ClientConnection pop() {
        std::unique_lock<std::mutex> lock(mutex_);
        ready_.wait(lock, [this] { return !clients_.empty(); });
        ClientConnection client = clients_.front();
        clients_.pop_front();
        return client;
    }

private:
    const size_t capacity_;
    std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<ClientConnection> clients_;
};

static bool should_log_counter(uint64_t count) {
    return count == 1 || (count & (count - 1)) == 0;
}

static bool resolve_group_id(const std::string& group_name, gid_t& group_id) {
    struct group entry;
    struct group* result = nullptr;
    std::vector<char> buffer(16384);
    int error = getgrnam_r(group_name.c_str(), &entry, buffer.data(),
                           buffer.size(), &result);
    if (error != 0 || result == nullptr) {
        std::cerr << "kaimo_authd: required socket group '" << group_name
                  << "' does not exist" << std::endl;
        return false;
    }
    group_id = entry.gr_gid;
    return true;
}

static bool configure_expected_peer_executable() {
    const char* configured = std::getenv("KAIMO_AUTHD_PEER_EXECUTABLE");
    const char* executable = configured && configured[0] != '\0'
        ? configured
        : "/opt/samba/sbin/smbd";
    struct stat details;
    if (stat(executable, &details) != 0 || !S_ISREG(details.st_mode) ||
        details.st_uid != 0 || (details.st_mode & 0022) != 0) {
        std::cerr << "kaimo_authd: trusted peer executable '" << executable
                  << "' must be a root-owned, non-writable regular file"
                  << std::endl;
        return false;
    }
    g_expected_peer_device = details.st_dev;
    g_expected_peer_inode = details.st_ino;
    const char* separator = std::strrchr(executable, '/');
    g_expected_peer_name = separator ? separator + 1 : executable;
    if (g_expected_peer_name.empty()) {
        std::cerr << "kaimo_authd: trusted peer executable has no basename"
                  << std::endl;
        return false;
    }
    return true;
}

static bool read_proc_identity(pid_t pid, const char* entry,
                               std::string& value) {
    char path[64];
    int path_length = std::snprintf(
        path, sizeof(path), "/proc/%ld/%s", static_cast<long>(pid), entry);
    if (path_length <= 0 || static_cast<size_t>(path_length) >= sizeof(path)) {
        return false;
    }

    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return false;
    char buffer[256];
    ssize_t bytes = read(fd, buffer, sizeof(buffer));
    int saved_errno = errno;
    close(fd);
    errno = saved_errno;
    if (bytes <= 0) return false;

    size_t length = static_cast<size_t>(bytes);
    for (size_t index = 0; index < length; ++index) {
        if (buffer[index] == '\0' || buffer[index] == '\n') {
            length = index;
            break;
        }
    }
    value.assign(buffer, length);
    return !value.empty();
}

static bool peer_has_expected_process_identity(pid_t pid) {
    std::string process_status;
    if (!read_proc_identity(pid, "stat", process_status)) {
        std::cerr << "kaimo_authd: cannot read peer process status pid="
                  << pid << ": " << std::strerror(errno) << std::endl;
        return false;
    }
    size_t name_start = process_status.find('(');
    size_t name_end = process_status.rfind(')');
    if (name_start == std::string::npos ||
        name_end == std::string::npos ||
        name_end <= name_start + 1) {
        return false;
    }
    std::string command_name =
        process_status.substr(name_start + 1, name_end - name_start - 1);
    bool exact = command_name == g_expected_peer_name;
    bool samba_process_title =
        command_name.size() > g_expected_peer_name.size() &&
        command_name.compare(0, g_expected_peer_name.size(),
                             g_expected_peer_name) == 0 &&
        (command_name[g_expected_peer_name.size()] == ':' ||
         command_name[g_expected_peer_name.size()] == ' ' ||
         command_name[g_expected_peer_name.size()] == '[');
    if (!exact && !samba_process_title) {
        std::cerr << "kaimo_authd: unexpected root peer process name pid="
                  << pid << " name='" << command_name << "'" << std::endl;
    }
    return exact || samba_process_title;
}

static bool canonicalize_and_validate_socket_parent(std::string& socket_path,
                                                    gid_t socket_group) {
    size_t separator = socket_path.find_last_of('/');
    if (socket_path.empty() || separator == std::string::npos ||
        separator + 1 == socket_path.size()) {
        std::cerr << "kaimo_authd: socket path must include a filename and "
                     "private parent directory" << std::endl;
        return false;
    }
    std::string parent = separator == 0
        ? "/"
        : socket_path.substr(0, separator);
    std::string filename = socket_path.substr(separator + 1);
    char resolved[PATH_MAX];
    if (realpath(parent.c_str(), resolved) == nullptr) {
        std::cerr << "kaimo_authd: cannot resolve socket directory '"
                  << parent << "': " << std::strerror(errno) << std::endl;
        return false;
    }

    struct stat details;
    if (lstat(resolved, &details) != 0 || !S_ISDIR(details.st_mode) ||
        details.st_uid != geteuid() || details.st_gid != socket_group ||
        (details.st_mode & S_IRWXU) != S_IRWXU ||
        (details.st_mode & (S_IRGRP | S_IXGRP)) !=
            (S_IRGRP | S_IXGRP) ||
        (details.st_mode & (S_IWGRP | S_IRWXO)) != 0) {
        std::cerr << "kaimo_authd: socket directory must be owned by uid "
                  << geteuid() << ", group " << socket_group
                  << ", and mode 0750 (or stricter without group write)"
                  << std::endl;
        return false;
    }

    socket_path = std::string(resolved);
    if (socket_path.back() != '/') socket_path.push_back('/');
    socket_path += filename;
    if (socket_path.size() >=
        sizeof(((struct sockaddr_un*)nullptr)->sun_path)) {
        std::cerr << "kaimo_authd: socket path exceeds Unix address limit"
                  << std::endl;
        return false;
    }
    return true;
}

static bool inspect_peer(int client_fd, PeerIdentity& peer) {
    struct ucred credentials;
    socklen_t length = sizeof(credentials);
    if (getsockopt(client_fd, SOL_SOCKET, SO_PEERCRED,
                   &credentials, &length) != 0 ||
        length != sizeof(credentials) || credentials.pid <= 0) {
        return false;
    }
    peer = {credentials.pid, credentials.uid, credentials.gid};

    // A non-root peer is bound below to the exact passwd UID named in the
    // request. Linux may deny /proc/<pid>/exe after a real UID transition
    // (non-dumpable process inside a capability-restricted container), so the
    // executable inode check is reserved for privileged peers that could
    // otherwise claim any session identity.
    if (credentials.uid != 0) return true;

    char executable_path[64];
    int path_length = std::snprintf(
        executable_path, sizeof(executable_path), "/proc/%ld/exe",
        static_cast<long>(credentials.pid));
    if (path_length <= 0 ||
        static_cast<size_t>(path_length) >= sizeof(executable_path)) {
        std::cerr << "kaimo_authd: invalid peer executable path for pid="
                  << credentials.pid << std::endl;
        return false;
    }

    struct stat executable;
    if (stat(executable_path, &executable) != 0) {
        int stat_error = errno;
        if ((stat_error == EACCES || stat_error == EPERM) &&
            peer_has_expected_process_identity(credentials.pid)) {
            return true;
        }
        std::cerr << "kaimo_authd: cannot inspect peer executable pid="
                  << credentials.pid << ": " << std::strerror(stat_error)
                  << std::endl;
        return false;
    }
    if (executable.st_dev != g_expected_peer_device ||
        executable.st_ino != g_expected_peer_inode) {
        std::cerr << "kaimo_authd: untrusted root peer executable pid="
                  << credentials.pid << " device=" << executable.st_dev
                  << " inode=" << executable.st_ino << std::endl;
        return false;
    }

    return true;
}

static bool peer_matches_username(const PeerIdentity& peer,
                                  const std::string& username) {
    // smbd may still be privileged at TREE_CONNECT. Its executable identity
    // binds a root request to Samba; non-root workers must additionally match
    // the exact POSIX account created for the authenticated Kaimo user.
    if (peer.uid == 0) return true;
    if (username.empty()) return false;

    struct passwd entry;
    struct passwd* result = nullptr;
    std::vector<char> buffer(16384);
    int error = getpwnam_r(username.c_str(), &entry, buffer.data(),
                           buffer.size(), &result);
    return error == 0 && result != nullptr &&
           entry.pw_uid == peer.uid &&
           username == entry.pw_name;
}

static void reject_peer(int client_fd, const PeerIdentity* peer) {
    (void)kaimo_local_send_frame(
        client_fd, KAIMO_LOCAL_OP_NONE, KAIMO_LOCAL_KIND_RESPONSE,
        KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER, nullptr, 0);
    uint64_t count = ++g_peer_rejections;
    if (should_log_counter(count)) {
        std::cerr << "kaimo_authd: rejected unauthorized local peer; total="
                  << count;
        if (peer != nullptr) {
            std::cerr << " pid=" << peer->pid
                      << " uid=" << peer->uid
                      << " gid=" << peer->gid;
        }
        std::cerr << std::endl;
    }
}

static bool cache_get(const std::string& key, bool& allow, uint32_t& granted_access) {
    bool hit = g_cache->get(key, allow, granted_access);
    uint64_t requests = ++g_cache_requests;
    if (requests >= 1024 && should_log_counter(requests)) {
        auto stats = g_cache->stats();
        std::cerr << "kaimo_authd: authorization cache requests=" << requests
                  << " hits=" << stats.hits
                  << " misses=" << stats.misses
                  << " entries=" << stats.entries
                  << " bytes=" << stats.accounted_bytes
                  << " expired=" << stats.expired << std::endl;
    }
    return hit;
}
static void cache_put(const std::string& key, bool allow, uint32_t granted_access) {
    auto result = g_cache->put(key, allow, granted_access);
    if (result.evicted > 0) {
        uint64_t total = g_cache_evictions.fetch_add(result.evicted) +
                         result.evicted;
        if (should_log_counter(total)) {
            std::cerr << "kaimo_authd: authorization cache evictions="
                      << total << std::endl;
        }
    }
    if (result.skipped_oversize) {
        uint64_t total = ++g_cache_oversize_skips;
        if (should_log_counter(total)) {
            std::cerr << "kaimo_authd: authorization cache oversize skips="
                      << total << std::endl;
        }
    }
}

static std::chrono::system_clock::time_point deadline(int secs) {
    return std::chrono::system_clock::now() + std::chrono::seconds(secs);
}

static bool read_bounded_size(const char* environment_name,
                              size_t default_value,
                              size_t minimum,
                              size_t maximum,
                              size_t& value) {
    const char* configured = std::getenv(environment_name);
    if (!configured || configured[0] == '\0') {
        value = default_value;
        return true;
    }

    char* end = nullptr;
    errno = 0;
    unsigned long long parsed = std::strtoull(configured, &end, 10);
    if (errno == ERANGE || end == configured || *end != '\0' ||
        parsed < minimum || parsed > maximum) {
        std::cerr << "kaimo_authd: invalid " << environment_name
                  << " (expected " << minimum << ".." << maximum << ")"
                  << std::endl;
        return false;
    }

    value = static_cast<size_t>(parsed);
    return true;
}

static bool configure_client_deadlines(int client_fd, size_t timeout_ms) {
    struct timeval timeout;
    timeout.tv_sec = static_cast<time_t>(timeout_ms / 1000);
    timeout.tv_usec = static_cast<suseconds_t>((timeout_ms % 1000) * 1000);
    return setsockopt(client_fd, SOL_SOCKET, SO_RCVTIMEO,
                      &timeout, sizeof(timeout)) == 0 &&
           setsockopt(client_fd, SOL_SOCKET, SO_SNDTIMEO,
                      &timeout, sizeof(timeout)) == 0;
}

// ---- Authz ----
static uint8_t do_connect(const std::string& user, const std::string& share) {
    AuthorizeConnectRequest req; req.set_username(user); req.set_share(share);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeConnect(&ctx, req, &reply);
    if (!st.ok()) { std::cerr << "kaimo_authd: AuthorizeConnect: " << st.error_message() << std::endl; return KAIMO_LOCAL_STATUS_ERROR; }
    return reply.allow() ? KAIMO_LOCAL_STATUS_ALLOW : KAIMO_LOCAL_STATUS_DENY;
}

struct OpenResult {
    uint8_t status;
    uint32_t granted_access;
};

static OpenResult do_open(const std::string& user, const std::string& share,
                          uint32_t access_mask, bool wants_create,
                          bool create_directory, bool directory_listing,
                          const std::string& path,
                          const std::string& cache_key) {
    bool cached;
    uint32_t cached_access = 0;
    if (cache_get(cache_key, cached, cached_access))
        return {
            cached ? KAIMO_LOCAL_STATUS_ALLOW : KAIMO_LOCAL_STATUS_DENY,
            cached_access
        };
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
        return {KAIMO_LOCAL_STATUS_ERROR, 0};
    }
    cache_put(cache_key, reply.allow(), reply.granted_access_mask());
    return {
        reply.allow() ? KAIMO_LOCAL_STATUS_ALLOW : KAIMO_LOCAL_STATUS_DENY,
        reply.granted_access_mask()
    };
}

static uint8_t do_delete(const std::string& user, const std::string& share,
                         bool isdir, const std::string& path) {
    AuthorizeDeleteRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path); req.set_is_directory(isdir);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeDelete(&ctx, req, &reply);
    if (!st.ok()) { std::cerr << "kaimo_authd: AuthorizeDelete: " << st.error_message() << std::endl; return KAIMO_LOCAL_STATUS_ERROR; }
    return reply.allow() ? KAIMO_LOCAL_STATUS_ALLOW : KAIMO_LOCAL_STATUS_DENY;
}

static uint8_t do_rename(const std::string& user, const std::string& share,
                         bool source_is_directory,
                         bool destination_exists,
                         bool destination_is_directory,
                         bool replace_intent,
                         const std::string& source_path,
                         const std::string& destination_path) {
    AuthorizeRenameRequest req;
    req.set_username(user);
    req.set_share(share);
    req.set_source_path(source_path);
    req.set_destination_path(destination_path);
    req.set_source_is_directory(source_is_directory);
    req.set_destination_exists(destination_exists);
    req.set_destination_is_directory(destination_is_directory);
    req.set_replace_intent(replace_intent);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(5));
    AuthorizeReply reply;
    grpc::Status st = g_authz->AuthorizeRename(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: AuthorizeRename: "
                  << st.error_message() << std::endl;
        return KAIMO_LOCAL_STATUS_ERROR;
    }
    return reply.allow() ? KAIMO_LOCAL_STATUS_ALLOW : KAIMO_LOCAL_STATUS_DENY;
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
static uint8_t do_snapenum(const std::string& user, const std::string& share,
                           const std::string& path,
                           std::vector<std::string>& tokens) {
    EnumerateSnapshotsRequest req;
    req.set_username(user); req.set_share(share); req.set_path(path);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(10));
    EnumerateSnapshotsReply reply;
    grpc::Status st = g_snapshot->EnumerateSnapshots(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: EnumerateSnapshots: " << st.error_message() << std::endl;
        return KAIMO_LOCAL_STATUS_ERROR;
    }
    tokens.reserve(static_cast<size_t>(reply.gmt_tokens_size()));
    for (const auto& token : reply.gmt_tokens()) tokens.push_back(token);
    return KAIMO_LOCAL_STATUS_OK;
}

struct SnapshotResolveResult {
    uint8_t status;
    std::string cache_path;
    uint64_t size;
};

static SnapshotResolveResult do_snapresolve(
    const std::string& user, const std::string& share,
    const std::string& token, const std::string& path) {
    ResolveVersionRequest req;
    req.set_username(user); req.set_share(share);
    req.set_gmt_token(token); req.set_path(path);
    grpc::ClientContext ctx; ctx.set_deadline(deadline(30));
    ResolveVersionReply reply;
    grpc::Status st = g_snapshot->ResolveVersion(&ctx, req, &reply);
    if (!st.ok()) {
        std::cerr << "kaimo_authd: ResolveVersion: " << st.error_message() << std::endl;
        return {KAIMO_LOCAL_STATUS_ERROR, {}, 0};
    }
    if (!reply.found()) return {KAIMO_LOCAL_STATUS_NOT_FOUND, {}, 0};
    if (reply.size() < 0) {
        std::cerr << "kaimo_authd: ResolveVersion returned a negative size"
                  << std::endl;
        return {KAIMO_LOCAL_STATUS_ERROR, {}, 0};
    }
    return {
        KAIMO_LOCAL_STATUS_OK,
        reply.cache_path(),
        static_cast<uint64_t>(reply.size())
    };
}

static bool read_string(kaimo_local_reader& reader, std::string& value) {
    const uint8_t* bytes = nullptr;
    uint32_t length = 0;
    if (!kaimo_local_reader_string(&reader, &bytes, &length)) return false;
    value.assign(reinterpret_cast<const char*>(bytes), length);
    return true;
}

static bool read_boolean(kaimo_local_reader& reader, bool& value) {
    uint8_t encoded = 0;
    if (!kaimo_local_reader_u8(&reader, &encoded) || encoded > 1) return false;
    value = encoded == 1;
    return true;
}

static void note_receive_failure() {
    if (errno == EAGAIN || errno == EWOULDBLOCK) {
            uint64_t count = ++g_receive_timeouts;
            if (should_log_counter(count)) {
                std::cerr << "kaimo_authd: receive deadline expired; total="
                          << count << std::endl;
            }
    }
}

static void handle_client(const ClientConnection& connection) {
    int cfd = connection.fd;
    kaimo_local_frame_header frame{};
    if (kaimo_local_read_frame_header(cfd, &frame) != 0) {
        note_receive_failure();
        close(cfd);
        return;
    }

    if (frame.kind != KAIMO_LOCAL_KIND_REQUEST ||
        frame.status != KAIMO_LOCAL_STATUS_NONE ||
        frame.operation == KAIMO_LOCAL_OP_NONE) {
        close(cfd);
        return;
    }

    std::vector<uint8_t> request(frame.payload_length);
    if (frame.payload_length != 0 &&
        kaimo_local_read_exact(cfd, request.data(), request.size()) != 0) {
        note_receive_failure();
        close(cfd);
        return;
    }

    kaimo_local_reader input;
    kaimo_local_reader_init(&input, request.data(), request.size());
    std::vector<uint8_t> response(KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD);
    kaimo_local_builder output;
    kaimo_local_builder_init(&output, response.data(), response.size());
    uint8_t status = KAIMO_LOCAL_STATUS_ERROR;
    std::string user, share, path, old_path, new_path, token;
    bool first = false, second = false, third = false, fourth = false;
    uint32_t access_mask = 0;

    if (!read_string(input, user) || !read_string(input, share)) {
        // The operation-specific parser below is deliberately skipped. A
        // syntactically invalid request receives the generic protocol error.
    } else if (!peer_matches_username(connection.peer, user)) {
        status = KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER;
        uint64_t count = ++g_peer_rejections;
        if (should_log_counter(count)) {
            std::cerr << "kaimo_authd: peer uid " << connection.peer.uid
                      << " cannot claim user '" << user
                      << "'; rejected=" << count << std::endl;
        }
    } else {
    switch (frame.operation) {
    case KAIMO_LOCAL_OP_CONNECT:
        if (kaimo_local_reader_finished(&input))
            status = do_connect(user, share);
        break;
    case KAIMO_LOCAL_OP_OPEN:
        if (kaimo_local_reader_u32(&input, &access_mask) &&
            read_boolean(input, first) && read_boolean(input, second) &&
            read_boolean(input, third) && read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            std::string cache_key(
                reinterpret_cast<const char*>(request.data()), request.size());
            OpenResult result = do_open(user, share, access_mask, first,
                                        second, third, path, cache_key);
            status = result.status;
            if (status == KAIMO_LOCAL_STATUS_ALLOW)
                kaimo_local_builder_u32(&output, result.granted_access);
        }
        break;
    case KAIMO_LOCAL_OP_DELETE_AUTH:
        if (read_boolean(input, first) && read_string(input, path) &&
            kaimo_local_reader_finished(&input))
            status = do_delete(user, share, first, path);
        break;
    case KAIMO_LOCAL_OP_RENAME_AUTH:
        if (read_boolean(input, first) && read_boolean(input, second) &&
            read_boolean(input, third) && read_boolean(input, fourth) &&
            read_string(input, old_path) && read_string(input, new_path) &&
            kaimo_local_reader_finished(&input))
            status = do_rename(user, share, first, second, third, fourth,
                               old_path, new_path);
        break;
    case KAIMO_LOCAL_OP_CLOSE:
        if (read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            ev_close(user, share, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_MKDIR:
        if (read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            ev_mkdir(user, share, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_DELETE:
        if (read_boolean(input, first) && read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            ev_delete(user, share, first, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_RENAME:
        if (read_boolean(input, first) && read_string(input, old_path) &&
            read_string(input, new_path) &&
            kaimo_local_reader_finished(&input)) {
            ev_rename(user, share, first, old_path, new_path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE:
        if (read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            std::vector<std::string> tokens;
            status = do_snapenum(user, share, path, tokens);
            if (status == KAIMO_LOCAL_STATUS_OK &&
                tokens.size() <= UINT32_MAX) {
                kaimo_local_builder_u32(
                    &output, static_cast<uint32_t>(tokens.size()));
                for (const auto& snapshot_token : tokens)
                    kaimo_local_builder_string(&output,
                                                snapshot_token.c_str());
                if (!output.valid) {
                    output.length = 0;
                    status = KAIMO_LOCAL_STATUS_ERROR;
                }
            } else if (status == KAIMO_LOCAL_STATUS_OK) {
                status = KAIMO_LOCAL_STATUS_ERROR;
            }
        }
        break;
    case KAIMO_LOCAL_OP_SNAPSHOT_RESOLVE:
        if (read_string(input, token) && read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            SnapshotResolveResult result =
                do_snapresolve(user, share, token, path);
            status = result.status;
            if (status == KAIMO_LOCAL_STATUS_OK &&
                (!kaimo_local_builder_string(
                     &output, result.cache_path.c_str()) ||
                 !kaimo_local_builder_u64(&output, result.size))) {
                output.length = 0;
                status = KAIMO_LOCAL_STATUS_ERROR;
            }
        }
        break;
    default:
        break;
    }
    }

    if (!output.valid && status != KAIMO_LOCAL_STATUS_ERROR) {
        output.length = 0;
        status = KAIMO_LOCAL_STATUS_ERROR;
    }
    (void)kaimo_local_send_frame(
        cfd, frame.operation, KAIMO_LOCAL_KIND_RESPONSE, status,
        response.data(), output.length);
    close(cfd);
}

int main() {
    signal(SIGPIPE, SIG_IGN);

    size_t worker_count = 0;
    size_t queue_capacity = 0;
    size_t io_timeout_ms = 0;
    size_t cache_max_entries = 0;
    size_t cache_max_bytes = 0;
    size_t cache_ttl_ms = 0;
    if (!read_bounded_size("KAIMO_AUTHD_WORKERS", 16, 1, 256,
                           worker_count) ||
        !read_bounded_size("KAIMO_AUTHD_QUEUE_CAPACITY", 64, 1, 4096,
                           queue_capacity) ||
        !read_bounded_size("KAIMO_AUTHD_IO_TIMEOUT_MS", 2000, 100, 60000,
                           io_timeout_ms) ||
        !read_bounded_size("KAIMO_AUTHD_CACHE_MAX_ENTRIES", 10000, 1, 1000000,
                           cache_max_entries) ||
        !read_bounded_size("KAIMO_AUTHD_CACHE_MAX_BYTES", 8388608, 1024,
                           1073741824, cache_max_bytes) ||
        !read_bounded_size("KAIMO_AUTHD_CACHE_TTL_MS", 3000, 100, 10000,
                           cache_ttl_ms)) {
        return 1;
    }
    g_cache = std::make_unique<kaimo::authd::DecisionCache>(
        std::chrono::milliseconds(cache_ttl_ms),
        cache_max_entries,
        cache_max_bytes);

    const char* addr_env = std::getenv("KAIMO_BRIDGE_ADDR");
    g_bridge_addr = addr_env ? addr_env : "kaimo_smb_bridge:5080";
    const char* sock_env = std::getenv("KAIMO_AUTHD_SOCK");
    std::string sock_path = sock_env ? sock_env : "/var/run/kaimo/authz.sock";
    const char* group_env = std::getenv("KAIMO_AUTHD_GROUP");
    std::string socket_group_name =
        group_env && group_env[0] != '\0' ? group_env : "kaimo-authd";
    gid_t socket_group = 0;
    if (!resolve_group_id(socket_group_name, socket_group) ||
        !configure_expected_peer_executable() ||
        !canonicalize_and_validate_socket_parent(sock_path, socket_group)) {
        return 1;
    }

    std::shared_ptr<grpc::Channel> channel;
    try {
        channel = kaimo::control_plane::create_mtls_channel(
            g_bridge_addr,
            "KAIMO_BRIDGE_RUNTIME_CERT",
            "/run/secrets/kaimo_bridge_runtime.crt",
            "KAIMO_BRIDGE_RUNTIME_KEY",
            "/run/secrets/kaimo_bridge_runtime.key");
    } catch (const std::exception& error) {
        std::cerr << "kaimo_authd: " << error.what() << std::endl;
        return 1;
    }
    g_authz = AuthzService::NewStub(channel);
    g_events = EventService::NewStub(channel);
    g_snapshot = SnapshotService::NewStub(channel);

    int sfd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (sfd < 0) { std::cerr << "kaimo_authd: socket() failed" << std::endl; return 1; }

    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    memcpy(addr.sun_path, sock_path.c_str(), sock_path.size() + 1);

    struct stat stale_socket;
    if (lstat(sock_path.c_str(), &stale_socket) == 0) {
        if (!S_ISSOCK(stale_socket.st_mode) ||
            stale_socket.st_uid != geteuid() ||
            unlink(sock_path.c_str()) != 0) {
            std::cerr << "kaimo_authd: refusing unsafe stale socket path '"
                      << sock_path << "'" << std::endl;
            close(sfd);
            return 1;
        }
    } else if (errno != ENOENT) {
        std::cerr << "kaimo_authd: cannot inspect socket path: "
                  << std::strerror(errno) << std::endl;
        close(sfd);
        return 1;
    }

    mode_t previous_umask = umask(0117);
    int bind_result = bind(sfd, (struct sockaddr*)&addr, sizeof(addr));
    umask(previous_umask);
    if (bind_result != 0) {
        std::cerr << "kaimo_authd: bind(" << sock_path << ") failed"
                  << std::endl;
        close(sfd);
        return 1;
    }
    if (chown(sock_path.c_str(), geteuid(), socket_group) != 0 ||
        chmod(sock_path.c_str(), 0660) != 0) {
        std::cerr << "kaimo_authd: cannot secure socket ownership/mode"
                  << std::endl;
        unlink(sock_path.c_str());
        close(sfd);
        return 1;
    }
    struct stat published_socket;
    if (lstat(sock_path.c_str(), &published_socket) != 0 ||
        !S_ISSOCK(published_socket.st_mode) ||
        published_socket.st_uid != geteuid() ||
        published_socket.st_gid != socket_group ||
        (published_socket.st_mode & 0777) != 0660) {
        std::cerr << "kaimo_authd: socket security verification failed"
                  << std::endl;
        unlink(sock_path.c_str());
        close(sfd);
        return 1;
    }
    if (listen(sfd, 128) != 0) {
        std::cerr << "kaimo_authd: listen() failed" << std::endl;
        unlink(sock_path.c_str());
        close(sfd);
        return 1;
    }

    BoundedClientQueue client_queue(queue_capacity);
    std::vector<std::thread> workers;
    workers.reserve(worker_count);
    for (size_t i = 0; i < worker_count; ++i) {
        workers.emplace_back([&client_queue] {
            for (;;) handle_client(client_queue.pop());
        });
    }

    std::cerr << "kaimo_authd: ready. socket=" << sock_path
              << " bridge=" << g_bridge_addr
              << " socket_group=" << socket_group_name
              << " workers=" << worker_count
              << " queue=" << queue_capacity
              << " io_timeout_ms=" << io_timeout_ms
              << " cache_entries=" << cache_max_entries
              << " cache_bytes=" << cache_max_bytes
              << " cache_ttl_ms=" << cache_ttl_ms << std::endl;

    for (;;) {
        int cfd = accept4(sfd, nullptr, nullptr, SOCK_CLOEXEC);
        if (cfd < 0) {
            if (errno != EINTR) {
                std::cerr << "kaimo_authd: accept() failed: "
                          << std::strerror(errno) << std::endl;
            }
            continue;
        }

        if (!configure_client_deadlines(cfd, io_timeout_ms)) {
            std::cerr << "kaimo_authd: failed to set client socket deadlines"
                      << std::endl;
            close(cfd);
            continue;
        }

        PeerIdentity peer{};
        if (!inspect_peer(cfd, peer)) {
            reject_peer(cfd, &peer);
            close(cfd);
            continue;
        }

        ClientConnection client{cfd, peer};
        if (!client_queue.try_push(client)) {
            (void)kaimo_local_send_frame(
                cfd, KAIMO_LOCAL_OP_NONE, KAIMO_LOCAL_KIND_RESPONSE,
                KAIMO_LOCAL_STATUS_OVERLOADED, nullptr, 0);
            close(cfd);
            uint64_t count = ++g_overload_rejections;
            if (should_log_counter(count)) {
                std::cerr << "kaimo_authd: worker queue full; rejected="
                          << count << std::endl;
            }
        }
    }
    return 0;
}
