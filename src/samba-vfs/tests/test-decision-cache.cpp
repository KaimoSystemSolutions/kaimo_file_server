#include <cassert>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <string>

#include "decision_cache.h"

using kaimo::authd::DecisionCache;

int main() {
    using namespace std::chrono_literals;
    const auto now = DecisionCache::TimePoint{};

    DecisionCache lru_cache(3s, 3, 1024 * 1024);
    lru_cache.put_at("a", true, 1, now);
    lru_cache.put_at("b", false, 2, now);
    lru_cache.put_at("c", true, 3, now);

    bool allow = false;
    uint32_t access = 0;
    assert(lru_cache.get_at("a", allow, access, now + 1ms));
    assert(allow && access == 1);

    auto insert = lru_cache.put_at("d", true, 4, now + 2ms);
    assert(insert.cached && insert.evicted == 1);
    assert(!lru_cache.get_at("b", allow, access, now + 3ms));
    assert(lru_cache.get_at("a", allow, access, now + 3ms));

    const std::string large_key(512, 'x');
    const size_t one_entry_budget =
        DecisionCache::estimated_entry_bytes(large_key);
    DecisionCache byte_cache(3s, 100, one_entry_budget);
    assert(byte_cache.put_at(large_key, true, 7, now).cached);
    auto byte_eviction =
        byte_cache.put_at(std::string(512, 'y'), false, 8, now);
    assert(byte_eviction.cached && byte_eviction.evicted == 1);
    assert(byte_cache.stats().accounted_bytes <= one_entry_budget);

    DecisionCache oversize_cache(3s, 100, one_entry_budget - 1);
    auto skipped = oversize_cache.put_at(large_key, true, 9, now);
    assert(!skipped.cached && skipped.skipped_oversize);
    assert(oversize_cache.stats().entries == 0);

    DecisionCache expiry_cache(3s, 100, 1024 * 1024);
    expiry_cache.put_at("expired", true, 10, now);
    assert(!expiry_cache.get_at("expired", allow, access, now + 3s));
    assert(expiry_cache.stats().entries == 0);
    assert(expiry_cache.stats().expired == 1);
    assert(expiry_cache.stats().misses == 1);
    assert(lru_cache.stats().hits == 2);

    std::cout << "decision cache tests passed" << std::endl;
    return 0;
}
