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

void test_gmt_label()
{
	char label[32];
	CHECK(kaimo_snapshot_gmt_label(0, label, sizeof(label)));
	CHECK(std::string(label) == "@GMT-1970.01.01-00.00.00");
	CHECK(kaimo_snapshot_gmt_label(1704164645, label, sizeof(label)));
	CHECK(std::string(label) == "@GMT-2024.01.02-03.04.05");

	/* 24 characters plus the terminating NUL must fit. */
	char exact[25];
	CHECK(kaimo_snapshot_gmt_label(1704164645, exact, sizeof(exact)));
	char small[24];
	CHECK(!kaimo_snapshot_gmt_label(1704164645, small, sizeof(small)));

	/* Five-digit years do not form a valid 24-character token. */
	CHECK(!kaimo_snapshot_gmt_label(253402300800LL, label, sizeof(label)));
}

void test_granted_access()
{
	const uint32_t read = KAIMO_FILE_GENERIC_READ;
	const uint32_t execute = KAIMO_FILE_GENERIC_EXECUTE;
	const uint32_t allowed = KAIMO_SNAPSHOT_ALLOWED_ACCESS;

	CHECK(allowed == (read | execute));
	CHECK((allowed & KAIMO_SNAPSHOT_MUTATING_ACCESS) == 0);

	/* Generic bits expand to their specific read/execute masks. */
	CHECK(kaimo_snapshot_granted_access(KAIMO_GENERIC_READ_ACCESS,
					    allowed) == read);
	CHECK(kaimo_snapshot_granted_access(KAIMO_GENERIC_EXECUTE_ACCESS,
					    allowed) == execute);
	CHECK(kaimo_snapshot_granted_access(KAIMO_MAXIMUM_ALLOWED_ACCESS,
					    allowed) == allowed);

	/* Never broader than requested, never broader than granted. */
	CHECK(kaimo_snapshot_granted_access(0x1U, allowed) == 0x1U);
	CHECK(kaimo_snapshot_granted_access(read, 0x1U) == 0x1U);
	CHECK(kaimo_snapshot_granted_access(0, allowed) == 0);

	/* Mutating rights are dropped even when requested and granted. */
	CHECK(kaimo_snapshot_granted_access(
		      KAIMO_SNAPSHOT_MUTATING_ACCESS | read, 0xffffffffU) == read);
	CHECK(kaimo_snapshot_granted_access(0xffffffffU, 0xffffffffU) ==
	      allowed);
}

void test_create_readonly()
{
	CHECK(kaimo_snapshot_create_readonly(KAIMO_FILE_GENERIC_READ,
					     KAIMO_FILE_OPEN, 0));
	CHECK(kaimo_snapshot_create_readonly(KAIMO_MAXIMUM_ALLOWED_ACCESS,
					     KAIMO_FILE_OPEN, 0x40U));

	for (uint32_t bit = 0; bit < 32; ++bit) {
		uint32_t mask = 1U << bit;
		if ((mask & KAIMO_SNAPSHOT_MUTATING_ACCESS) == 0)
			continue;
		errno = 0;
		CHECK(!kaimo_snapshot_create_readonly(mask, KAIMO_FILE_OPEN, 0));
		CHECK(errno == EROFS);
	}
	/* Every disposition other than FILE_OPEN may create or truncate. */
	for (uint32_t disposition : {0U, 2U, 3U, 4U, 5U}) {
		errno = 0;
		CHECK(!kaimo_snapshot_create_readonly(KAIMO_FILE_GENERIC_READ,
						      disposition, 0));
		CHECK(errno == EROFS);
	}
	errno = 0;
	CHECK(!kaimo_snapshot_create_readonly(KAIMO_FILE_GENERIC_READ,
					      KAIMO_FILE_OPEN,
					      KAIMO_FILE_DELETE_ON_CLOSE));
	CHECK(errno == EROFS);
}

}  // namespace

int main()
{
	test_cache_relative_path();
	test_lease_scope();
	test_open_flags();
	test_resolve_decode();
	test_gmt_label();
	test_granted_access();
	test_create_readonly();
	std::cout << "snapshot access tests passed" << std::endl;
	return 0;
}
