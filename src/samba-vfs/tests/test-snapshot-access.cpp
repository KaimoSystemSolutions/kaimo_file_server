#include "snapshot_access.h"

#include "kaimo_check.h"

#include <cerrno>
#include <cstdint>
#include <iostream>
#include <string>
#include <vector>

namespace {

std::vector<uint8_t> string_field(const std::string &value)
{
	std::vector<uint8_t> out = {
		static_cast<uint8_t>(value.size() >> 24),
		static_cast<uint8_t>(value.size() >> 16),
		static_cast<uint8_t>(value.size() >> 8),
		static_cast<uint8_t>(value.size())};
	out.insert(out.end(), value.begin(), value.end());
	return out;
}

std::vector<uint8_t> resolution(const std::string &path, const std::string &lease)
{
	std::vector<uint8_t> out = string_field(path);
	for (int i = 0; i < 7; ++i)
		out.push_back(0);
	out.push_back(42);
	auto lease_field = string_field(lease);
	out.insert(out.end(), lease_field.begin(), lease_field.end());
	return out;
}

int decode(uint8_t status, const std::vector<uint8_t> &payload, char *cache,
	   size_t cache_size, char *lease, size_t lease_size)
{
	return kaimo_snapshot_resolve_decode(
		status, payload.empty() ? nullptr : payload.data(),
		static_cast<uint32_t>(payload.size()), cache, cache_size, lease,
		lease_size);
}

void test_cache_relative_path()
{
	CHECK(kaimo_cache_relative_path_valid("a"));
	CHECK(kaimo_cache_relative_path_valid("share/@GMT-2024.01.02-03.04.05/user/f.txt"));
	CHECK(kaimo_cache_relative_path_valid("a/.hidden/..dots/x."));
	for (const char *bad : {"", "/abs", "a//b", "a/", "./a", "a/./b", "a/../b",
				"..", ".", "a\\b", "a\x01" "b", "a\x7f", "a\tb"})
		CHECK(!kaimo_cache_relative_path_valid(bad));
	CHECK(!kaimo_cache_relative_path_valid(nullptr));
}

void test_lease_scope()
{
	CHECK(kaimo_snapshot_lease_scope_length("share/token/user/file") == 11);
	CHECK(kaimo_snapshot_lease_scope_length("share/token/user") == 11);
	errno = 0;
	CHECK(kaimo_snapshot_lease_scope_length("share/token") == -1);
	CHECK(errno == EACCES);
	errno = 0;
	CHECK(kaimo_snapshot_lease_scope_length("single") == -1);
	CHECK(errno == EACCES);
	errno = 0;
	CHECK(kaimo_snapshot_lease_scope_length("../token/user") == -1);
	CHECK(errno == EACCES);
	CHECK(kaimo_snapshot_lease_scope_length(nullptr) == -1);
}

void test_open_flags()
{
	int output = -1;
	CHECK(kaimo_snapshot_open_flags_readonly(O_RDONLY, &output));
	CHECK(output == O_RDONLY);
	CHECK(kaimo_snapshot_open_flags_readonly(O_RDONLY | O_NOFOLLOW | O_DIRECTORY, &output));
	CHECK(output == (O_RDONLY | O_NOFOLLOW | O_DIRECTORY));
	for (int bad : {O_WRONLY, O_RDWR, O_RDONLY | O_CREAT, O_RDONLY | O_EXCL,
			O_RDONLY | O_TRUNC, O_RDONLY | O_APPEND
#ifdef O_TMPFILE
			, O_TMPFILE | O_RDONLY
#endif
	     }) {
		output = 12345;
		errno = 0;
		CHECK(!kaimo_snapshot_open_flags_readonly(bad, &output));
		CHECK(errno == EROFS);
		CHECK(output == 12345);
	}
	CHECK(!kaimo_snapshot_open_flags_readonly(O_RDONLY, nullptr));
}

void test_resolve_decode()
{
	char cache[64];
	char lease[16];

	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("s/t/u/f", "lease-1"),
		     cache, sizeof(cache), lease, sizeof(lease)) == 1);
	CHECK(std::string(cache) == "s/t/u/f");
	CHECK(std::string(lease) == "lease-1");

	CHECK(decode(KAIMO_LOCAL_STATUS_NOT_FOUND, {}, cache, sizeof(cache),
		     lease, sizeof(lease)) == 0);
	CHECK(decode(KAIMO_LOCAL_STATUS_NOT_FOUND, {1}, cache, sizeof(cache),
		     lease, sizeof(lease)) == -1);
	for (uint8_t status : {KAIMO_LOCAL_STATUS_ERROR, KAIMO_LOCAL_STATUS_DENY,
			       KAIMO_LOCAL_STATUS_ALLOW, KAIMO_LOCAL_STATUS_OVERLOADED})
		CHECK(decode(status, resolution("a/b/c", "l"), cache, sizeof(cache),
			     lease, sizeof(lease)) == -1);

	// Structural errors.
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("", "l"), cache,
		     sizeof(cache), lease, sizeof(lease)) == -1);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a/b/c", ""), cache,
		     sizeof(cache), lease, sizeof(lease)) == -1);
	auto truncated = resolution("a/b/c", "lease");
	truncated.pop_back();
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, truncated, cache, sizeof(cache),
		     lease, sizeof(lease)) == -1);
	auto trailing = resolution("a/b/c", "lease");
	trailing.push_back(0);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, trailing, cache, sizeof(cache),
		     lease, sizeof(lease)) == -1);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, string_field("a/b/c"), cache,
		     sizeof(cache), lease, sizeof(lease)) == -1);

	// Output bounds: the terminating NUL must fit.
	errno = 0;
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution(std::string(64, 'x'), "l"),
		     cache, sizeof(cache), lease, sizeof(lease)) == -1);
	CHECK(errno == ENAMETOOLONG);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution(std::string(63, 'x'), "l"),
		     cache, sizeof(cache), lease, sizeof(lease)) == 1);
	errno = 0;
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a", std::string(16, 'l')),
		     cache, sizeof(cache), lease, sizeof(lease)) == -1);
	CHECK(errno == ENAMETOOLONG);

	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a", "l"), nullptr, 1,
		     lease, sizeof(lease)) == -1);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a", "l"), cache, 0,
		     lease, sizeof(lease)) == -1);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a", "l"), cache,
		     sizeof(cache), nullptr, 1) == -1);
	CHECK(decode(KAIMO_LOCAL_STATUS_OK, resolution("a", "l"), cache,
		     sizeof(cache), lease, 0) == -1);
}

}  // namespace

int main()
{
	test_cache_relative_path();
	test_lease_scope();
	test_open_flags();
	test_resolve_decode();
	std::cout << "snapshot access tests passed" << std::endl;
	return 0;
}
