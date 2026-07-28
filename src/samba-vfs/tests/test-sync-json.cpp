#include <cassert>
#include <string>

#include "sync_json.h"

int main() {
    using namespace kaimo::sync_json;

    assert(valid_username("marco.hanisch"));
    assert(valid_username("User-01"));
    assert(!valid_username("-option"));
    assert(!valid_username("bad\tname"));
    assert(!valid_username(std::string(33, 'a')));
    assert(!valid_username(std::string("\xC0\xAF", 2)));

    assert(valid_share_name("documents_01"));
    assert(!valid_share_name(".hidden"));
    assert(!valid_share_name("trailing."));
    assert(!valid_share_name("GLOBAL"));
    assert(!valid_share_name("bad/name"));

    assert(valid_absolute_path("/data/storage/pool/share"));
    assert(!valid_absolute_path("relative/path"));
    assert(!valid_absolute_path("/data/\nshare"));

    assert(dialect_rank("SMB2_02") == 0);
    assert(dialect_rank("SMB3_11") == 4);
    assert(dialect_rank("NT1") == -1);

    assert(quote("plain") == "\"plain\"");
    assert(quote("a\"b\\c") == "\"a\\\"b\\\\c\"");
    return 0;
}
