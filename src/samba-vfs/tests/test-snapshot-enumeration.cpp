#include "snapshot_enumeration.h"

#include <array>
#include <cassert>
#include <cerrno>
#include <cstdint>
#include <string>
#include <vector>

static constexpr const char *Token = "@GMT-2026.07.29-12.34.56";

static std::vector<uint8_t> response(
	uint32_t advertised_count, uint32_t actual_count,
	const char *token = Token)
{
	std::vector<uint8_t> payload(
		4U + actual_count *
		(4U + KAIMO_LOCAL_SNAPSHOT_TOKEN_BYTES));
	kaimo_local_builder builder;
	kaimo_local_builder_init(
		&builder, payload.data(), payload.size());
	assert(kaimo_local_builder_u32(&builder, advertised_count));
	for (uint32_t i = 0; i < actual_count; ++i)
		assert(kaimo_local_builder_string(&builder, token));
	assert(builder.valid);
	payload.resize(builder.length);
	return payload;
}

static void expect_valid(
	const std::vector<uint8_t>& payload, uint32_t expected_count)
{
	uint32_t count = UINT32_MAX;
	assert(kaimo_snapshot_enumeration_validate(
		payload.data(), payload.size(), &count));
	assert(count == expected_count);
}

static void expect_invalid(
	const std::vector<uint8_t>& payload, int expected_errno)
{
	uint32_t count = UINT32_MAX;
	errno = 0;
	assert(!kaimo_snapshot_enumeration_validate(
		payload.data(), payload.size(), &count));
	assert(count == 0);
	assert(errno == expected_errno);
}

int main()
{
	assert(KAIMO_LOCAL_MAX_SNAPSHOT_ENUMERATION_PAYLOAD == 57348U);
	assert(KAIMO_LOCAL_MAX_SNAPSHOT_ENUMERATION_PAYLOAD <=
	       KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD);
	expect_valid(response(0, 0), 0);
	expect_valid(response(1, 1), 1);
	expect_valid(
		response(KAIMO_LOCAL_MAX_SNAPSHOT_LABELS,
			 KAIMO_LOCAL_MAX_SNAPSHOT_LABELS),
		KAIMO_LOCAL_MAX_SNAPSHOT_LABELS);

	expect_invalid(
		response(KAIMO_LOCAL_MAX_SNAPSHOT_LABELS + 1U, 0),
		EMSGSIZE);
	expect_invalid(response(2, 1), EPROTO);
	expect_invalid(response(1, 2), EPROTO);

	auto truncated = response(1, 1);
	truncated.pop_back();
	expect_invalid(truncated, EPROTO);

	auto trailing = response(1, 1);
	trailing.push_back(0);
	expect_invalid(trailing, EPROTO);

	expect_invalid(
		response(1, 1, "@GMT-2026.07.29/12.34.56"),
		EPROTO);
	expect_invalid(response(1, 1, "not-a-shadow-copy-token!"), EPROTO);

	uint32_t count = 0;
	errno = 0;
	assert(!kaimo_snapshot_enumeration_validate(nullptr, 0, &count));
	assert(errno == EPROTO);
	errno = 0;
	auto valid = response(0, 0);
	assert(!kaimo_snapshot_enumeration_validate(
		valid.data(), valid.size(), nullptr));
	assert(errno == EINVAL);
	return 0;
}
