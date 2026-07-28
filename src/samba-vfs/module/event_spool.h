#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <dirent.h>
#include <fcntl.h>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <sys/random.h>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

namespace kaimo::authd {

struct SpoolEvent {
    std::string id;
    uint8_t operation = 0;
    std::vector<uint8_t> payload;
    uint32_t attempts = 0;
    uint64_t next_attempt_ms = 0;
};

class EventSpool {
public:
    EventSpool(std::string root, size_t max_pending, size_t max_dead,
               uint32_t max_attempts, uint64_t retry_base_ms,
               uint64_t retry_max_ms)
        : root_(std::move(root)),
          pending_(root_ + "/pending"),
          dead_(root_ + "/dead"),
          max_pending_(max_pending),
          max_dead_(max_dead),
          max_attempts_(max_attempts),
          retry_base_ms_(retry_base_ms),
          retry_max_ms_(retry_max_ms) {}

    bool initialize(std::string& error) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!secure_directory(root_, true, error) ||
            !secure_directory(pending_, true, error) ||
            !secure_directory(dead_, true, error)) {
            return false;
        }
        remove_temporary_files(pending_);
        pending_count_ = count_records(pending_);
        dead_count_ = count_records(dead_);
        return true;
    }

    bool enqueue(uint8_t operation, const uint8_t* payload, size_t payload_size,
                 std::string& event_id, std::string& error) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (pending_count_ >= max_pending_) {
            error = "pending spool capacity reached";
            return false;
        }

        SpoolEvent event;
        if (!random_id(event.id, error)) return false;
        event.operation = operation;
        event.payload.assign(payload, payload + payload_size);
        event.next_attempt_ms = now_ms();
        const std::string path = record_path(pending_, event.id);
        if (!atomic_write(path, event, error)) return false;
        ++pending_count_;
        event_id = event.id;
        return true;
    }

    std::optional<SpoolEvent> next_ready(uint64_t now, std::string& error) {
        std::lock_guard<std::mutex> lock(mutex_);
        std::vector<std::string> names = record_names(pending_);
        std::optional<SpoolEvent> selected;
        for (const auto& name : names) {
            SpoolEvent candidate;
            if (!read_record(record_path(pending_, name), candidate, error)) {
                return std::nullopt;
            }
            if (candidate.next_attempt_ms > now) continue;
            if (!selected ||
                candidate.next_attempt_ms < selected->next_attempt_ms ||
                (candidate.next_attempt_ms == selected->next_attempt_ms &&
                 candidate.id < selected->id)) {
                selected = std::move(candidate);
            }
        }
        return selected;
    }

    bool delivered(const SpoolEvent& event, std::string& error) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (unlink(record_path(pending_, event.id).c_str()) != 0) {
            error = std::string("unlink delivered event: ") + std::strerror(errno);
            return false;
        }
        if (!sync_directory(pending_, error)) return false;
        if (pending_count_ > 0) --pending_count_;
        return true;
    }

    bool retry_or_dead(SpoolEvent event, uint64_t now, bool& dead_lettered,
                       std::string& error) {
        std::lock_guard<std::mutex> lock(mutex_);
        ++event.attempts;
        if (event.attempts >= max_attempts_) {
            if (!atomic_write(record_path(dead_, event.id), event, error)) {
                return false;
            }
            if (unlink(record_path(pending_, event.id).c_str()) != 0) {
                error = std::string("unlink dead-lettered event: ") +
                        std::strerror(errno);
                return false;
            }
            if (!sync_directory(pending_, error)) return false;
            if (pending_count_ > 0) --pending_count_;
            ++dead_count_;
            prune_dead();
            dead_lettered = true;
            return true;
        }

        uint64_t multiplier = uint64_t{1}
            << std::min<uint32_t>(event.attempts - 1, 20);
        uint64_t delay = retry_base_ms_ > retry_max_ms_ / multiplier
            ? retry_max_ms_
            : std::min(retry_max_ms_, retry_base_ms_ * multiplier);
        event.next_attempt_ms = now + delay;
        dead_lettered = false;
        return atomic_write(record_path(pending_, event.id), event, error);
    }

    size_t pending_count() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return pending_count_;
    }

    size_t dead_count() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return dead_count_;
    }

    static uint64_t now_ms() {
        return static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch()).count());
    }

private:
    static constexpr uint8_t magic_[4] = {'K', 'E', 'V', '1'};
    static constexpr size_t header_size_ = 4 + 1 + 4 + 8 + 4 + 32;

    static void append_u32(std::vector<uint8_t>& bytes, uint32_t value) {
        for (int shift = 24; shift >= 0; shift -= 8)
            bytes.push_back(static_cast<uint8_t>(value >> shift));
    }

    static void append_u64(std::vector<uint8_t>& bytes, uint64_t value) {
        for (int shift = 56; shift >= 0; shift -= 8)
            bytes.push_back(static_cast<uint8_t>(value >> shift));
    }

    static uint32_t read_u32(const uint8_t* value) {
        return (uint32_t(value[0]) << 24) | (uint32_t(value[1]) << 16) |
               (uint32_t(value[2]) << 8) | uint32_t(value[3]);
    }

    static uint64_t read_u64(const uint8_t* value) {
        uint64_t result = 0;
        for (size_t i = 0; i < 8; ++i) result = (result << 8) | value[i];
        return result;
    }

    static bool write_all(int fd, const uint8_t* data, size_t length,
                          std::string& error) {
        size_t offset = 0;
        while (offset < length) {
            ssize_t written = write(fd, data + offset, length - offset);
            if (written < 0 && errno == EINTR) continue;
            if (written <= 0) {
                error = std::string("write spool record: ") + std::strerror(errno);
                return false;
            }
            offset += static_cast<size_t>(written);
        }
        return true;
    }

    static bool random_id(std::string& id, std::string& error) {
        uint8_t bytes[16];
        size_t offset = 0;
        while (offset < sizeof(bytes)) {
            ssize_t result = getrandom(bytes + offset, sizeof(bytes) - offset, 0);
            if (result < 0 && errno == EINTR) continue;
            if (result <= 0) {
                error = std::string("getrandom event id: ") + std::strerror(errno);
                return false;
            }
            offset += static_cast<size_t>(result);
        }
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        static constexpr char hex[] = "0123456789abcdef";
        id.resize(32);
        for (size_t i = 0; i < sizeof(bytes); ++i) {
            id[i * 2] = hex[bytes[i] >> 4];
            id[i * 2 + 1] = hex[bytes[i] & 0x0f];
        }
        return true;
    }

    static bool secure_directory(const std::string& path, bool create,
                                 std::string& error) {
        struct stat details;
        if (lstat(path.c_str(), &details) != 0) {
            if (errno != ENOENT || !create || mkdir(path.c_str(), 0700) != 0) {
                error = "cannot create/inspect spool directory '" + path +
                        "': " + std::strerror(errno);
                return false;
            }
            if (lstat(path.c_str(), &details) != 0) {
                error = "cannot verify spool directory '" + path + "'";
                return false;
            }
        }
        if (!S_ISDIR(details.st_mode) || details.st_uid != geteuid() ||
            (details.st_mode & 0077) != 0) {
            error = "spool directory must be owner-only, non-symlink, and owned by authd: " +
                    path;
            return false;
        }
        return true;
    }

    static bool sync_directory(const std::string& path, std::string& error) {
        int fd = open(path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (fd < 0) {
            error = std::string("open spool directory for fsync: ") +
                    std::strerror(errno);
            return false;
        }
        int result = fsync(fd);
        int saved = errno;
        close(fd);
        if (result != 0) {
            error = std::string("fsync spool directory: ") + std::strerror(saved);
            return false;
        }
        return true;
    }

    static std::string record_path(const std::string& directory,
                                   const std::string& id) {
        return directory + "/" + id + ".evt";
    }

    static std::vector<std::string> record_names(const std::string& directory) {
        std::vector<std::string> names;
        DIR* dir = opendir(directory.c_str());
        if (!dir) return names;
        while (dirent* entry = readdir(dir)) {
            std::string name(entry->d_name);
            if (name.size() == 36 &&
                name.compare(name.size() - 4, 4, ".evt") == 0) {
                names.push_back(name.substr(0, 32));
            }
        }
        closedir(dir);
        return names;
    }

    static size_t count_records(const std::string& directory) {
        return record_names(directory).size();
    }

    static void remove_temporary_files(const std::string& directory) {
        DIR* dir = opendir(directory.c_str());
        if (!dir) return;
        while (dirent* entry = readdir(dir)) {
            std::string name(entry->d_name);
            if (name.size() > 4 &&
                name.compare(name.size() - 4, 4, ".tmp") == 0) {
                unlink((directory + "/" + name).c_str());
            }
        }
        closedir(dir);
    }

    bool atomic_write(const std::string& final_path, const SpoolEvent& event,
                      std::string& error) const {
        if (event.id.size() != 32 || event.payload.size() > UINT32_MAX) {
            error = "invalid spool record";
            return false;
        }
        std::vector<uint8_t> bytes;
        bytes.reserve(header_size_ + event.payload.size());
        bytes.insert(bytes.end(), magic_, magic_ + sizeof(magic_));
        bytes.push_back(event.operation);
        append_u32(bytes, event.attempts);
        append_u64(bytes, event.next_attempt_ms);
        append_u32(bytes, static_cast<uint32_t>(event.payload.size()));
        bytes.insert(bytes.end(), event.id.begin(), event.id.end());
        bytes.insert(bytes.end(), event.payload.begin(), event.payload.end());

        const std::string directory =
            final_path.substr(0, final_path.find_last_of('/'));
        const std::string temporary = directory + "/." + event.id + "." +
            std::to_string(static_cast<long long>(getpid())) + ".tmp";
        int fd = open(temporary.c_str(),
                      O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                      0600);
        if (fd < 0) {
            error = std::string("create spool record: ") + std::strerror(errno);
            return false;
        }
        bool ok = write_all(fd, bytes.data(), bytes.size(), error);
        if (ok && fsync(fd) != 0) {
            error = std::string("fsync spool record: ") + std::strerror(errno);
            ok = false;
        }
        int saved = errno;
        close(fd);
        errno = saved;
        if (!ok || rename(temporary.c_str(), final_path.c_str()) != 0) {
            if (ok) error = std::string("publish spool record: ") +
                            std::strerror(errno);
            unlink(temporary.c_str());
            return false;
        }
        return sync_directory(directory, error);
    }

    static bool read_record(const std::string& path, SpoolEvent& event,
                            std::string& error) {
        struct stat details;
        if (lstat(path.c_str(), &details) != 0 || !S_ISREG(details.st_mode) ||
            details.st_uid != geteuid() || (details.st_mode & 0077) != 0 ||
            details.st_size < static_cast<off_t>(header_size_) ||
            details.st_size > static_cast<off_t>(header_size_ + 65536)) {
            error = "unsafe or invalid spool record: " + path;
            return false;
        }
        std::vector<uint8_t> bytes(static_cast<size_t>(details.st_size));
        int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
        if (fd < 0) {
            error = std::string("open spool record: ") + std::strerror(errno);
            return false;
        }
        size_t offset = 0;
        while (offset < bytes.size()) {
            ssize_t count = read(fd, bytes.data() + offset, bytes.size() - offset);
            if (count < 0 && errno == EINTR) continue;
            if (count <= 0) break;
            offset += static_cast<size_t>(count);
        }
        close(fd);
        if (offset != bytes.size() ||
            std::memcmp(bytes.data(), magic_, sizeof(magic_)) != 0) {
            error = "truncated or incompatible spool record: " + path;
            return false;
        }
        size_t cursor = 4;
        event.operation = bytes[cursor++];
        event.attempts = read_u32(bytes.data() + cursor);
        cursor += 4;
        event.next_attempt_ms = read_u64(bytes.data() + cursor);
        cursor += 8;
        uint32_t payload_size = read_u32(bytes.data() + cursor);
        cursor += 4;
        event.id.assign(reinterpret_cast<const char*>(bytes.data() + cursor), 32);
        cursor += 32;
        if (cursor + payload_size != bytes.size()) {
            error = "invalid spool payload length: " + path;
            return false;
        }
        event.payload.assign(bytes.begin() + static_cast<ptrdiff_t>(cursor),
                             bytes.end());
        return true;
    }

    void prune_dead() {
        if (dead_count_ <= max_dead_) return;
        std::vector<std::string> names = record_names(dead_);
        std::sort(names.begin(), names.end());
        size_t remove_count = dead_count_ - max_dead_;
        for (size_t i = 0; i < remove_count && i < names.size(); ++i) {
            if (unlink(record_path(dead_, names[i]).c_str()) == 0) --dead_count_;
        }
        std::string ignored;
        sync_directory(dead_, ignored);
    }

    std::string root_;
    std::string pending_;
    std::string dead_;
    size_t max_pending_;
    size_t max_dead_;
    uint32_t max_attempts_;
    uint64_t retry_base_ms_;
    uint64_t retry_max_ms_;
    mutable std::mutex mutex_;
    size_t pending_count_ = 0;
    size_t dead_count_ = 0;
};

}  // namespace kaimo::authd
