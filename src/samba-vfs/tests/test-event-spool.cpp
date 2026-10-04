#include "event_spool.h"

#include "kaimo_check.h"
#include <cstdlib>
#include <iostream>
#include <string>
#include <sys/stat.h>
#include <unistd.h>

using kaimo::authd::EventSpool;

static std::string temporary_directory() {
    char path[] = "/tmp/kaimo-event-spool-XXXXXX";
    char* result = mkdtemp(path);
    CHECK(result != nullptr);
    CHECK(chmod(result, 0700) == 0);
    return result;
}

int main() {
    const std::string root = temporary_directory();
    std::string error;
    EventSpool spool(root, 2, 1, 3, 10, 100);
    CHECK(spool.initialize(error));

    const uint8_t first_payload[] = {1, 2, 3};
    std::string first_id;
    CHECK(spool.enqueue(5, first_payload, sizeof(first_payload), first_id, error));
    CHECK(first_id.size() == 32);
    CHECK(spool.pending_count() == 1);

    // A fresh object recovers the fsync-published pending record after restart.
    EventSpool recovered(root, 2, 1, 3, 10, 100);
    CHECK(recovered.initialize(error));
    auto first = recovered.next_ready(EventSpool::now_ms(), error);
    CHECK(first.has_value());
    CHECK(first->id == first_id);
    CHECK(first->operation == 5);
    CHECK(first->payload.size() == sizeof(first_payload));

    bool dead = false;
    const uint64_t first_retry_at = EventSpool::now_ms();
    CHECK(recovered.retry_or_dead(*first, first_retry_at, dead, error));
    CHECK(!dead);
    CHECK(!recovered.next_ready(first_retry_at + 9, error).has_value());
    auto retry = recovered.next_ready(first_retry_at + 10, error);
    CHECK(retry.has_value());
    CHECK(retry->attempts == 1);

    const uint64_t second_retry_at = EventSpool::now_ms();
    CHECK(recovered.retry_or_dead(*retry, second_retry_at, dead, error));
    CHECK(!dead);
    retry = recovered.next_ready(second_retry_at + 20, error);
    CHECK(retry.has_value());
    CHECK(recovered.retry_or_dead(*retry, EventSpool::now_ms(), dead, error));
    CHECK(dead);
    CHECK(recovered.pending_count() == 0);
    CHECK(recovered.dead_count() == 1);

    const uint8_t second_payload[] = {9};
    std::string second_id;
    CHECK(recovered.enqueue(6, second_payload, sizeof(second_payload),
                             second_id, error));
    auto second = recovered.next_ready(EventSpool::now_ms(), error);
    CHECK(second.has_value());
    CHECK(recovered.delivered(*second, error));
    CHECK(recovered.pending_count() == 0);

    // Pending capacity is a hard, predictable bound.
    std::string id;
    CHECK(recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));
    CHECK(recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));
    CHECK(!recovered.enqueue(7, second_payload, sizeof(second_payload), id, error));

    std::cout << "event spool tests passed" << std::endl;
    return 0;
}
