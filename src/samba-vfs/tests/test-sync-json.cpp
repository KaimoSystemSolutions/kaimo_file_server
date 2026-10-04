#include "kaimo_check.h"
#include <string>

#include "sync_json.h"

int main() {
    using namespace kaimo::sync_json;

    CHECK(valid_username("marco.hanisch"));
    CHECK(valid_username("User-01"));
    CHECK(!valid_username("-option"));
    CHECK(!valid_username("bad\tname"));
    CHECK(!valid_username(std::string(33, 'a')));
    CHECK(!valid_username(std::string("\xC0\xAF", 2)));

    CHECK(valid_share_name("documents_01"));
    CHECK(!valid_share_name(".hidden"));
    CHECK(!valid_share_name("trailing."));
    CHECK(!valid_share_name("GLOBAL"));
    CHECK(!valid_share_name("bad/name"));

    CHECK(valid_absolute_path("/data/storage/pool/share"));
    CHECK(!valid_absolute_path("relative/path"));
    CHECK(!valid_absolute_path("/data/\nshare"));

    CHECK(dialect_rank("SMB2_02") == 0);
    CHECK(dialect_rank("SMB3_11") == 4);
    CHECK(dialect_rank("NT1") == -1);

    CHECK(quote("plain") == "\"plain\"");
    CHECK(quote("a\"b\\c") == "\"a\\\"b\\\\c\"");

    // Every JSON escape the reconcilers' jq parser must round-trip.
    CHECK(quote("\b\f\n\r\t") == "\"\\b\\f\\n\\r\\t\"");
    CHECK(quote(std::string("\x01\x1f", 2)) == "\"\\u0001\\u001F\"");
    CHECK(quote(std::string("\0", 1)) == "\"\\u0000\"");
    CHECK(quote("\xC3\xA4") == "\"\xC3\xA4\"");  // UTF-8 passes through
    CHECK(std::string(boolean(true)) == "true");
    CHECK(std::string(boolean(false)) == "false");

    CHECK(dialect_rank("SMB2_10") == 1);
    CHECK(dialect_rank("SMB3_00") == 2);
    CHECK(dialect_rank("SMB3_02") == 3);
    CHECK(dialect_rank("smb3_11") == -1);

    // UTF-8 validation: valid multi-byte, overlongs, surrogates, range, truncation.
    CHECK(valid_utf8("\xC3\xA4\xE2\x82\xAC\xF0\x9F\x98\x80"));
    CHECK(valid_utf8("\xF4\x8F\xBF\xBF"));
    CHECK(!valid_utf8("\xE0\x80\x80"));
    CHECK(!valid_utf8("\xF0\x80\x80\x80"));
    CHECK(!valid_utf8("\xED\xA0\x80"));
    CHECK(!valid_utf8("\xF4\x90\x80\x80"));
    CHECK(!valid_utf8("\xF5\x80\x80\x80"));
    CHECK(!valid_utf8("\xC3"));
    CHECK(!valid_utf8("\xE2\x82"));
    CHECK(!valid_utf8("\xC3\x28"));
    CHECK(!valid_utf8("\x80"));

    CHECK(valid_absolute_path("/data/\xC3\xA4rger"));
    CHECK(!valid_absolute_path(""));
    CHECK(!valid_absolute_path("/data/\x7f"));
    CHECK(!valid_absolute_path(std::string(4097, '/')));
    CHECK(valid_absolute_path(std::string(4096, '/')));
    for (const char *reserved : {"homes", "Printers", "print$", "IPC$"})
        CHECK(!valid_share_name(reserved));
    CHECK(!valid_share_name(""));
    CHECK(!valid_share_name(std::string(65, 's')));
    CHECK(valid_share_name(std::string(64, 's')));
    CHECK(!valid_username(""));
    CHECK(valid_username("0day"));
    return 0;
}
