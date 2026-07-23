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

class BoundedClientQueue {
public:
    explicit BoundedClientQueue(size_t capacity) : capacity_(capacity) {}

    bool try_push(int client_fd) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (clients_.size() >= capacity_) return false;
        clients_.push_back(client_fd);
        ready_.notify_one();
        return true;
    }

    int pop() {
        std::unique_lock<std::mutex> lock(mutex_);
        ready_.wait(lock, [this] { return !clients_.empty(); });
        int client_fd = clients_.front();
        clients_.pop_front();
        return client_fd;
    }

private:
    const size_t capacity_;
    std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<int> clients_;
};

static bool should_log_counter(uint64_t count) {
    return count == 1 || (count & (count - 1)) == 0;
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

static void handle_client(int cfd) {
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

    switch (frame.operation) {
    case KAIMO_LOCAL_OP_CONNECT:
        if (read_string(input, user) && read_string(input, share) &&
            kaimo_local_reader_finished(&input))
            status = do_connect(user, share);
        break;
    case KAIMO_LOCAL_OP_OPEN:
        if (read_string(input, user) && read_string(input, share) &&
            kaimo_local_reader_u32(&input, &access_mask) &&
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
        if (read_string(input, user) && read_string(input, share) &&
            read_boolean(input, first) && read_string(input, path) &&
            kaimo_local_reader_finished(&input))
            status = do_delete(user, share, first, path);
        break;
    case KAIMO_LOCAL_OP_RENAME_AUTH:
        if (read_string(input, user) && read_string(input, share) &&
            read_boolean(input, first) && read_boolean(input, second) &&
            read_boolean(input, third) && read_boolean(input, fourth) &&
            read_string(input, old_path) && read_string(input, new_path) &&
            kaimo_local_reader_finished(&input))
            status = do_rename(user, share, first, second, third, fourth,
                               old_path, new_path);
        break;
    case KAIMO_LOCAL_OP_CLOSE:
        if (read_string(input, user) && read_string(input, share) &&
            read_string(input, path) && kaimo_local_reader_finished(&input)) {
            ev_close(user, share, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_MKDIR:
        if (read_string(input, user) && read_string(input, share) &&
            read_string(input, path) && kaimo_local_reader_finished(&input)) {
            ev_mkdir(user, share, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_DELETE:
        if (read_string(input, user) && read_string(input, share) &&
            read_boolean(input, first) && read_string(input, path) &&
            kaimo_local_reader_finished(&input)) {
            ev_delete(user, share, first, path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_RENAME:
        if (read_string(input, user) && read_string(input, share) &&
            read_boolean(input, first) && read_string(input, old_path) &&
            read_string(input, new_path) &&
            kaimo_local_reader_finished(&input)) {
            ev_rename(user, share, first, old_path, new_path);
            status = KAIMO_LOCAL_STATUS_OK;
        }
        break;
    case KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE:
        if (read_string(input, user) && read_string(input, share) &&
            read_string(input, path) && kaimo_local_reader_finished(&input)) {
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
        if (read_string(input, user) && read_string(input, share) &&
            read_string(input, token) && read_string(input, path) &&
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

        if (!client_queue.try_push(cfd)) {
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
