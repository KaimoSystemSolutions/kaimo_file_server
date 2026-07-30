#include "event_spool.h"

#include <cassert>
#include <cstdlib>
#include <iostream>
#include <string>
#include <sys/stat.h>
#include <unistd.h>

using kaimo::authd::EventSpool;

static std::string temporary_directory() {
    char path[] = "/tmp/kaimo-event-spool-XXXXXX";
    char* result = mkdtemp(path);
    assert(result != nullptr);
    assert(chmod(result, 0700) == 0);
    return result;
}

int main() {
    const std::string root = temporary_directory();
    std::string error;
    EventSpool spool(root, 2, 1, 3, 10, 100);
    assert(spool.initialize(error));

    const uint8_t first_payload[] = {1, 2, 3};
    std::string first_id;
    assert(spool.enqueue(5, first_payload, sizeof(first_payload), first_id, error));
    assert(first_id.size() == 32);
    assert(spool.pending_count() == 1);

    // A fresh object recovers the fsync-published pending record after restart.
    EventSpool recovered(root, 2, 1, 3, 10, 100);
    assert(recovered.initialize(error));
    auto first = recovered.next_ready(EventSpool::now_ms(), error);
    assert(first.has_value());
    assert(first->id == first_id);
    assert(first->operation == 5);
    assert(first->payload.size() == sizeof(first_payload));

    bool dead = false;
    const uint64_t first_retry_at = EventSpool::now_ms();
    assert(recovered.retry_or_dead(*first, first_retry_at, dead, error));
    assert(!dead);
    assert(!recovered.next_ready(first_retry_at + 9, error).has_value());
    auto retry = recovered.next_ready(first_retry_at + 10, error);
    assert(retry.has_value());
    assert(retry->attempts == 1);

    const uint64_t second_retry_at = EventSpool::now_ms();
    assert(recovered.retry_or_dead(*retry, second_retry_at, dead, error));
    assert(!dead);
    retry = recovered.next_ready(second_retry_at + 20, error);
    assert(retry.has_value());
    assert(recovered.retry_or_dead(*retry, EventSpool::now_ms(), dead, error));
    assert(dead);
    assert(recovered.pending_count() == 0);
    assert(recovered.dead_count() == 1);

    const uint8_t second_payload[] = {9};
    std::string second_id;
    assert(recovered.enqueue(6, second_payload, sizeof(second_payload),
                             second_id, error));
    auto second = recovered.next_ready(EventSpool::now_ms(), error);
    assert(second.has_value());
    assert(recovered.delivered(*second, error));
    assert(recovered.pending_count() == 0);

    // Pending capacity is a hard, predictable bound.
    std::string id;
    assert(recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));
    assert(recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));
    assert(!recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));

    std::cout << "event spool tests passed" << std::endl;
    return 0;
}
