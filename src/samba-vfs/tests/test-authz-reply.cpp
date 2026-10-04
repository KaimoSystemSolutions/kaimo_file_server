#include "authz_reply.h"

#include "kaimo_check.h"

#include <cstdint>
#include <iostream>
#include <vector>

namespace {

struct Decoded {
	kaimo_authz_verdict verdict;
	uint32_t granted;
	bool recycle;
	unsigned int depth;
};

Decoded decode(uint8_t operation, uint8_t status,
	       const std::vector<uint8_t> &payload)
{
	Decoded result{KAIMO_AUTHZ_DENY, 0xdeadbeef, false, 99};
	result.verdict = kaimo_authz_decode_reply(
		operation, status, payload.empty() ? nullptr : payload.data(),
		static_cast<uint32_t>(payload.size()), &result.granted,
		&result.recycle, &result.depth);
	return result;
}

std::vector<uint8_t> be32(uint32_t value)
{
	return {static_cast<uint8_t>(value >> 24), static_cast<uint8_t>(value >> 16),
		static_cast<uint8_t>(value >> 8), static_cast<uint8_t>(value)};
}

kaimo_local_frame_header header(uint8_t operation, uint8_t kind,
				uint8_t status, uint32_t length)
{
	kaimo_local_frame_header frame{};
	frame.operation = operation;
	frame.kind = kind;
	frame.status = status;
	frame.payload_length = length;
	return frame;
}

void test_response_header_acceptance()
{
	const uint8_t open = KAIMO_LOCAL_OP_OPEN;
	auto ok = header(open, KAIMO_LOCAL_KIND_RESPONSE, KAIMO_LOCAL_STATUS_ALLOW, 4);
	CHECK(kaimo_authz_response_header_acceptable(&ok, open, 4));
	CHECK(!kaimo_authz_response_header_acceptable(&ok, open, 3));
	CHECK(!kaimo_authz_response_header_acceptable(nullptr, open, 4));

	auto request = header(open, KAIMO_LOCAL_KIND_REQUEST, KAIMO_LOCAL_STATUS_ALLOW, 0);
	CHECK(!kaimo_authz_response_header_acceptable(&request, open, 4));

	auto other = header(KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_KIND_RESPONSE,
			    KAIMO_LOCAL_STATUS_ALLOW, 0);
	CHECK(!kaimo_authz_response_header_acceptable(&other, open, 4));

	for (uint8_t status : {KAIMO_LOCAL_STATUS_ERROR, KAIMO_LOCAL_STATUS_OVERLOADED,
			       KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER}) {
		auto none = header(KAIMO_LOCAL_OP_NONE, KAIMO_LOCAL_KIND_RESPONSE, status, 0);
		CHECK(kaimo_authz_response_header_acceptable(&none, open, 0));
	}
	for (uint8_t status : {KAIMO_LOCAL_STATUS_ALLOW, KAIMO_LOCAL_STATUS_DENY,
			       KAIMO_LOCAL_STATUS_OK, KAIMO_LOCAL_STATUS_NOT_FOUND}) {
		auto none = header(KAIMO_LOCAL_OP_NONE, KAIMO_LOCAL_KIND_RESPONSE, status, 0);
		CHECK(!kaimo_authz_response_header_acceptable(&none, open, 0));
	}
}

void test_open_replies()
{
	const uint8_t op = KAIMO_LOCAL_OP_OPEN;
	auto allowed = decode(op, KAIMO_LOCAL_STATUS_ALLOW, be32(0x00120089));
	CHECK(allowed.verdict == KAIMO_AUTHZ_ALLOW);
	CHECK(allowed.granted == 0x00120089);

	auto full = decode(op, KAIMO_LOCAL_STATUS_ALLOW, be32(KAIMO_SAMBA_SPECIFIC_ACCESS));
	CHECK(full.verdict == KAIMO_AUTHZ_ALLOW);
	CHECK(full.granted == KAIMO_SAMBA_SPECIFIC_ACCESS);

	auto zero = decode(op, KAIMO_LOCAL_STATUS_ALLOW, be32(0));
	CHECK(zero.verdict == KAIMO_AUTHZ_ALLOW && zero.granted == 0);

	// Generic, MAXIMUM_ALLOWED and SACL bits are never a valid grant.
	for (uint32_t bad : {0x10000000U, 0x02000000U, 0x01000000U, 0x00200000U}) {
		auto rejected = decode(op, KAIMO_LOCAL_STATUS_ALLOW, be32(bad));
		CHECK(rejected.verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(rejected.granted == 0xdeadbeef);
	}
	CHECK(decode(op, KAIMO_LOCAL_STATUS_ALLOW, {}).verdict == KAIMO_AUTHZ_MALFORMED);
	CHECK(decode(op, KAIMO_LOCAL_STATUS_ALLOW, {0, 0, 1}).verdict == KAIMO_AUTHZ_MALFORMED);
	auto trailing = be32(1);
	trailing.push_back(0);
	CHECK(decode(op, KAIMO_LOCAL_STATUS_ALLOW, trailing).verdict == KAIMO_AUTHZ_MALFORMED);

	auto payload = be32(1);
	CHECK(kaimo_authz_decode_reply(op, KAIMO_LOCAL_STATUS_ALLOW, payload.data(), 4,
				       nullptr, nullptr, nullptr) == KAIMO_AUTHZ_MALFORMED);
}

void test_delete_replies()
{
	const uint8_t op = KAIMO_LOCAL_OP_DELETE_AUTH;
	auto permanent = decode(op, KAIMO_LOCAL_STATUS_ALLOW, {0, 0});
	CHECK(permanent.verdict == KAIMO_AUTHZ_ALLOW);
	CHECK(!permanent.recycle && permanent.depth == 0);

	auto recycle = decode(op, KAIMO_LOCAL_STATUS_ALLOW, {1, 1});
	CHECK(recycle.verdict == KAIMO_AUTHZ_ALLOW);
	CHECK(recycle.recycle && recycle.depth == 1);

	const std::vector<std::vector<uint8_t>> malformed = {
		{}, {1}, {2, 0}, {1, KAIMO_RECYCLE_MAX_ROOT_DEPTH + 1}, {1, 0, 0}};
	for (const auto &payload : malformed) {
		auto result = decode(op, KAIMO_LOCAL_STATUS_ALLOW, payload);
		CHECK(result.verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(result.depth == 99);
	}
	const uint8_t bytes[] = {1, 0};
	uint32_t granted = 0;
	bool recycle_flag = false;
	unsigned int depth = 0;
	CHECK(kaimo_authz_decode_reply(op, KAIMO_LOCAL_STATUS_ALLOW, bytes, 2,
				       &granted, nullptr, &depth) == KAIMO_AUTHZ_MALFORMED);
	CHECK(kaimo_authz_decode_reply(op, KAIMO_LOCAL_STATUS_ALLOW, bytes, 2,
				       &granted, &recycle_flag, nullptr) == KAIMO_AUTHZ_MALFORMED);
}

void test_empty_allow_and_other_statuses()
{
	for (uint8_t op : {KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_OP_RENAME_AUTH}) {
		CHECK(decode(op, KAIMO_LOCAL_STATUS_ALLOW, {}).verdict == KAIMO_AUTHZ_ALLOW);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_ALLOW, {0}).verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_DENY, {}).verdict == KAIMO_AUTHZ_DENY);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_DENY, {0}).verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_ERROR, {}).verdict == KAIMO_AUTHZ_UNAVAILABLE);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_OVERLOADED, {}).verdict == KAIMO_AUTHZ_UNAVAILABLE);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_ERROR, {1}).verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER, {}).verdict ==
		      KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_OK, {}).verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_NOT_FOUND, {}).verdict == KAIMO_AUTHZ_MALFORMED);
		CHECK(decode(op, KAIMO_LOCAL_STATUS_NONE, {}).verdict == KAIMO_AUTHZ_MALFORMED);
	}
}

void test_fail_mode_policy()
{
	const uint8_t ops[] = {KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_OP_OPEN,
			       KAIMO_LOCAL_OP_RENAME_AUTH, KAIMO_LOCAL_OP_DELETE_AUTH};
	for (uint8_t op : ops) {
		for (bool fail_open : {false, true}) {
			CHECK(kaimo_authz_permits(KAIMO_AUTHZ_ALLOW, op, fail_open));
			CHECK(!kaimo_authz_permits(KAIMO_AUTHZ_DENY, op, fail_open));
			CHECK(!kaimo_authz_permits(KAIMO_AUTHZ_MALFORMED, op, fail_open));
		}
		CHECK(!kaimo_authz_permits(KAIMO_AUTHZ_UNAVAILABLE, op, false));
	}
	CHECK(kaimo_authz_permits(KAIMO_AUTHZ_UNAVAILABLE, KAIMO_LOCAL_OP_CONNECT, true));
	CHECK(kaimo_authz_permits(KAIMO_AUTHZ_UNAVAILABLE, KAIMO_LOCAL_OP_OPEN, true));
	CHECK(kaimo_authz_permits(KAIMO_AUTHZ_UNAVAILABLE, KAIMO_LOCAL_OP_RENAME_AUTH, true));
	// The recycle disposition is unknown during an outage: never guess.
	CHECK(!kaimo_authz_permits(KAIMO_AUTHZ_UNAVAILABLE, KAIMO_LOCAL_OP_DELETE_AUTH, true));
	CHECK(!kaimo_authz_permits(static_cast<kaimo_authz_verdict>(7),
				   KAIMO_LOCAL_OP_CONNECT, true));

	CHECK(kaimo_authz_failopen_configured("1"));
	CHECK(kaimo_authz_failopen_configured("1yes"));
	CHECK(!kaimo_authz_failopen_configured(nullptr));
	CHECK(!kaimo_authz_failopen_configured(""));
	CHECK(!kaimo_authz_failopen_configured("0"));
	CHECK(!kaimo_authz_failopen_configured("true"));
}

}  // namespace

int main()
{
	test_response_header_acceptance();
	test_open_replies();
	test_delete_replies();
	test_empty_allow_and_other_statuses();
	test_fail_mode_policy();
	std::cout << "authz reply tests passed" << std::endl;
	return 0;
}
