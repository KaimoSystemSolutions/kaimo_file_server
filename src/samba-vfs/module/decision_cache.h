#pragma once

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <list>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>

namespace kaimo::authd {

class DecisionCache {
public:
    using Clock = std::chrono::steady_clock;
    using TimePoint = Clock::time_point;

    struct PutResult {
        bool cached;
        size_t evicted;
        bool skipped_oversize;
    };

    struct Stats {
        size_t entries;
        size_t accounted_bytes;
        uint64_t evictions;
        uint64_t expired;
        uint64_t oversize_skips;
        uint64_t hits;
        uint64_t misses;
    };

    DecisionCache(std::chrono::milliseconds ttl,
                  size_t maximum_entries,
                  size_t maximum_accounted_bytes)
        : ttl_(ttl),
          maximum_entries_(maximum_entries),
          maximum_accounted_bytes_(maximum_accounted_bytes) {
        const size_t budget_limited_entries =
            maximum_accounted_bytes_ / estimated_entry_bytes("");
        entries_.reserve(std::min(maximum_entries_, budget_limited_entries));
    }

    bool get(const std::string& key, bool& allow, uint32_t& granted_access) {
        return get_at(key, allow, granted_access, Clock::now());
    }

    bool get_at(const std::string& key,
                bool& allow,
                uint32_t& granted_access,
                TimePoint now) {
        std::lock_guard<std::mutex> lock(mutex_);
        sweep_if_due_locked(now);

        auto found = entries_.find(key);
        if (found == entries_.end()) {
            ++misses_;
            return false;
        }
        if (now >= found->second.expiry) {
            erase_locked(found, true);
            ++misses_;
            return false;
        }

        lru_.splice(lru_.begin(), lru_, found->second.lru_position);
        ++hits_;
        allow = found->second.allow;
        granted_access = found->second.granted_access;
        return true;
    }

    PutResult put(const std::string& key,
                  bool allow,
                  uint32_t granted_access) {
        return put_at(key, allow, granted_access, Clock::now());
    }

    PutResult put_at(const std::string& key,
                     bool allow,
                     uint32_t granted_access,
                     TimePoint now) {
        std::lock_guard<std::mutex> lock(mutex_);
        sweep_if_due_locked(now);

        auto existing = entries_.find(key);
        if (existing != entries_.end()) {
            existing->second.allow = allow;
            existing->second.granted_access = granted_access;
            existing->second.expiry = now + ttl_;
            lru_.splice(lru_.begin(), lru_, existing->second.lru_position);
            return {true, 0, false};
        }

        const size_t entry_bytes = estimated_entry_bytes(key);
        if (entry_bytes > maximum_accounted_bytes_) {
            ++oversize_skips_;
            return {false, 0, true};
        }

        size_t evicted = 0;
        while (!lru_.empty() &&
               (entries_.size() >= maximum_entries_ ||
                accounted_bytes_ + entry_bytes > maximum_accounted_bytes_)) {
            erase_lru_locked();
            ++evicted;
            ++evictions_;
        }

        lru_.push_front(key);
        Entry entry{
            allow,
            granted_access,
            now + ttl_,
            lru_.begin(),
            entry_bytes,
        };
        entries_.emplace(key, std::move(entry));
        accounted_bytes_ += entry_bytes;
        return {true, evicted, false};
    }

    Stats stats() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return {
            entries_.size(),
            accounted_bytes_,
            evictions_,
            expired_,
            oversize_skips_,
            hits_,
            misses_,
        };
    }

    static size_t estimated_entry_bytes(const std::string& key) {
        // The map and LRU each own a key string. Include a conservative fixed
        // node/allocation allowance so the configured budget covers metadata
        // as well as attacker-controlled path bytes.
        return sizeof(Entry) + (2 * sizeof(std::string)) +
               (8 * sizeof(void*)) + (2 * key.size());
    }

private:
    struct Entry {
        bool allow;
        uint32_t granted_access;
        TimePoint expiry;
        std::list<std::string>::iterator lru_position;
        size_t accounted_bytes;
    };

    using EntryMap = std::unordered_map<std::string, Entry>;

    void sweep_if_due_locked(TimePoint now) {
        if (now < next_sweep_) return;

        for (auto entry = entries_.begin(); entry != entries_.end();) {
            if (now >= entry->second.expiry) {
                auto expired = entry++;
                erase_locked(expired, true);
            } else {
                ++entry;
            }
        }

        auto interval = std::min(
            ttl_,
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::seconds(1)));
        next_sweep_ = now + interval;
    }

    void erase_lru_locked() {
        auto found = entries_.find(lru_.back());
        if (found != entries_.end()) erase_locked(found, false);
    }

    void erase_locked(EntryMap::iterator entry, bool expired) {
        accounted_bytes_ -= entry->second.accounted_bytes;
        lru_.erase(entry->second.lru_position);
        entries_.erase(entry);
        if (expired) ++expired_;
    }

    const std::chrono::milliseconds ttl_;
    const size_t maximum_entries_;
    const size_t maximum_accounted_bytes_;
    mutable std::mutex mutex_;
    EntryMap entries_;
    std::list<std::string> lru_;
    size_t accounted_bytes_ = 0;
    TimePoint next_sweep_ = TimePoint::min();
    uint64_t evictions_ = 0;
    uint64_t expired_ = 0;
    uint64_t oversize_skips_ = 0;
    uint64_t hits_ = 0;
    uint64_t misses_ = 0;
};

}  // namespace kaimo::authd
