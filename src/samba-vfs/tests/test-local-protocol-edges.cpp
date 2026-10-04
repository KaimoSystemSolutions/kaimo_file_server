// Boundary and rejection paths of local_protocol.h that the wire round-trip
// test (test-local-protocol.cpp) does not reach: the complete UTF-8 table,
// identity validators, builder overflow, every header rejection of both the
// blocking and the deadline-bound readers, and deadline arithmetic.
#include "local_protocol.h"

#include "kaimo_check.h"

#include <cerrno>
#include <climits>
#include <cstdint>
#include <iostream>
#include <string>
#include <vector>

#include <sys/socket.h>
#include <unistd.h>

namespace {

bool utf8(const std::vector<uint8_t> &bytes)
{
	return kaimo_local_valid_utf8(bytes.data(), bytes.size());
}

struct Pair {
	int writer;
	int reader;
	Pair()
	{
		int fds[2];
		CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, fds) == 0);
		writer = fds[0];
		reader = fds[1];
	}
	~Pair()
	{
		if (writer >= 0)
			close(writer);
		if (reader >= 0)
			close(reader);
	}
	void send(const std::vector<uint8_t> &bytes)
	{
		CHECK(kaimo_local_write_all(writer, bytes.data(), bytes.size()) == 0);
	}
	void hang_up()
	{
		close(writer);
		writer = -1;
	}
};

std::vector<uint8_t> header(uint8_t magic0, uint8_t version, uint8_t operation,
			    uint8_t kind, uint8_t status, uint32_t length)
{
	return {magic0, 'A', 'I', 'M', version, operation, kind, status,
		static_cast<uint8_t>(length >> 24), static_cast<uint8_t>(length >> 16),
		static_cast<uint8_t>(length >> 8), static_cast<uint8_t>(length)};
}

kaimo_local_deadline deadline_ms(uint32_t milliseconds)
{
	kaimo_local_deadline deadline{};
	CHECK(kaimo_local_deadline_init(&deadline, milliseconds) == 0);
	return deadline;
}

void test_utf8_table()
{
	CHECK(utf8({}));
	CHECK(utf8({'a', 0x7f}));
	CHECK(utf8({0xc2, 0x80}));                 // U+0080
	CHECK(utf8({0xdf, 0xbf}));                 // U+07FF
	CHECK(utf8({0xe0, 0xa0, 0x80}));           // U+0800
	CHECK(utf8({0xe1, 0x80, 0x80}));
	CHECK(utf8({0xed, 0x9f, 0xbf}));           // U+D7FF
	CHECK(utf8({0xef, 0xbf, 0xbf}));           // U+FFFF
	CHECK(utf8({0xf0, 0x90, 0x80, 0x80}));     // U+10000
	CHECK(utf8({0xf1, 0x80, 0x80, 0x80}));
	CHECK(utf8({0xf4, 0x8f, 0xbf, 0xbf}));     // U+10FFFF

	CHECK(!utf8({0x80}));                      // lone continuation
	CHECK(!utf8({0xc0, 0x80}));                // overlong
	CHECK(!utf8({0xc1, 0xbf}));                // overlong
	CHECK(!utf8({0xc2}));                      // truncated
	CHECK(!utf8({0xc2, 0x41}));                // bad continuation
	CHECK(!utf8({0xe0, 0x80, 0x80}));          // overlong 3-byte
	CHECK(!utf8({0xed, 0xa0, 0x80}));          // surrogate
	CHECK(!utf8({0xe1, 0x41, 0x80}));          // bad second byte
	CHECK(!utf8({0xe1, 0x80, 0x41}));          // bad third byte
	CHECK(!utf8({0xe1, 0x80}));                // truncated
	CHECK(!utf8({0xf0, 0x80, 0x80, 0x80}));    // overlong 4-byte
	CHECK(!utf8({0xf4, 0x90, 0x80, 0x80}));    // above U+10FFFF
	CHECK(!utf8({0xf1, 0x41, 0x80, 0x80}));
	CHECK(!utf8({0xf1, 0x80, 0x41, 0x80}));
	CHECK(!utf8({0xf1, 0x80, 0x80, 0x41}));
	CHECK(!utf8({0xf1, 0x80, 0x80}));          // truncated
	CHECK(!utf8({0xf5, 0x80, 0x80, 0x80}));    // invalid lead
	CHECK(!utf8({0xff}));
}

void test_identity_validators()
{
	CHECK(kaimo_local_valid_username("a"));
	CHECK(kaimo_local_valid_username("Alice.Smith_1-2"));
	CHECK(!kaimo_local_valid_username(nullptr));
	CHECK(!kaimo_local_valid_username(""));
	CHECK(!kaimo_local_valid_username(".alice"));
	CHECK(!kaimo_local_valid_username("-alice"));
	CHECK(!kaimo_local_valid_username("al ice"));
	CHECK(!kaimo_local_valid_username(std::string(33, 'a').c_str()));

	CHECK(kaimo_local_valid_share("Data-1_x.y"));
	CHECK(!kaimo_local_valid_share(nullptr));
	CHECK(!kaimo_local_valid_share(""));
	CHECK(!kaimo_local_valid_share("trailing."));
	CHECK(!kaimo_local_valid_share("team$"));
	CHECK(!kaimo_local_valid_share(std::string(65, 's').c_str()));
	for (const char *reserved : {"global", "GLOBAL", "Homes", "printers", "PRINT$", "IpC$"})
		CHECK(!kaimo_local_valid_share(reserved));
	CHECK(kaimo_local_valid_share("globalx"));
	CHECK(kaimo_local_valid_share("home"));
}

void test_builder_and_reader_limits()
{
	uint8_t storage[16];
	kaimo_local_builder builder;
	kaimo_local_builder_init(&builder, storage, sizeof(storage));
	CHECK(kaimo_local_builder_u8(&builder, 7));
	CHECK(kaimo_local_builder_u64(&builder, 0x0102030405060708ULL));
	CHECK(builder.length == 9);
	CHECK(!kaimo_local_builder_u64(&builder, 1));  // 9 + 8 > 16
	CHECK(!builder.valid);
	CHECK(!kaimo_local_builder_u8(&builder, 1));   // stays invalid
	errno = 0;

	kaimo_local_builder null_builder;
	kaimo_local_builder_init(&null_builder, nullptr, 0);
	CHECK(!null_builder.valid);

	std::vector<uint8_t> big(KAIMO_LOCAL_MAX_STRING_BYTES + 64);
	kaimo_local_builder big_builder;
	kaimo_local_builder_init(&big_builder, big.data(), big.size());
	std::string too_long(KAIMO_LOCAL_MAX_STRING_BYTES + 1, 'x');
	CHECK(!kaimo_local_builder_string(&big_builder, too_long.c_str()));
	CHECK(errno == EMSGSIZE);
	kaimo_local_builder_init(&big_builder, big.data(), big.size());
	CHECK(kaimo_local_builder_string(&big_builder, nullptr));  // empty string
	CHECK(big_builder.length == 4);

	uint8_t u64_bytes[8] = {1, 2, 3, 4, 5, 6, 7, 8};
	kaimo_local_reader reader;
	kaimo_local_reader_init(&reader, u64_bytes, sizeof(u64_bytes));
	uint64_t value = 0;
	CHECK(kaimo_local_reader_u64(&reader, &value));
	CHECK(value == 0x0102030405060708ULL);
	CHECK(kaimo_local_reader_finished(&reader));
	uint8_t byte = 0;
	CHECK(!kaimo_local_reader_u8(&reader, &byte));
	CHECK(!kaimo_local_reader_finished(&reader));

	kaimo_local_reader_init(&reader, nullptr, 4);
	CHECK(!reader.valid);
	CHECK(!kaimo_local_reader_u8(&reader, &byte));

	// Declared string longer than the protocol maximum.
	uint8_t oversized[] = {0x00, 0x00, 0x20, 0x00};
	const uint8_t *text = nullptr;
	uint32_t text_length = 0;
	kaimo_local_reader_init(&reader, oversized, sizeof(oversized));
	CHECK(!kaimo_local_reader_string(&reader, &text, &text_length));
	CHECK(errno == EPROTO);
}

void test_frame_send_validation()
{
	Pair pair;
	uint8_t payload[1] = {0};
	auto deadline = deadline_ms(1000);
	struct Case {
		uint8_t operation;
		uint8_t kind;
		const uint8_t *payload;
		size_t length;
	};
	const Case invalid[] = {
		{KAIMO_LOCAL_OP_CONNECT, 0, payload, 1},
		{KAIMO_LOCAL_OP_CONNECT, 3, payload, 1},
		{KAIMO_LOCAL_OP_MAX + 1, KAIMO_LOCAL_KIND_REQUEST, payload, 1},
		{KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_KIND_REQUEST, nullptr, 1},
		{KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_KIND_REQUEST, payload,
		 KAIMO_LOCAL_MAX_REQUEST_PAYLOAD + 1},
		{KAIMO_LOCAL_OP_CONNECT, KAIMO_LOCAL_KIND_RESPONSE, payload,
		 KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD + 1},
	};
	for (const Case &item : invalid) {
		errno = 0;
		CHECK(kaimo_local_send_frame(pair.writer, item.operation, item.kind, 0,
					     item.payload, item.length) == -1);
		CHECK(errno == EMSGSIZE);
		errno = 0;
		CHECK(kaimo_local_send_frame_until(pair.writer, item.operation, item.kind, 0,
						   item.payload, item.length, &deadline) == -1);
		CHECK(errno == EMSGSIZE);
	}

	// A peer that hung up: the write fails with EPIPE instead of SIGPIPE.
	Pair closed;
	close(closed.reader);
	closed.reader = -1;
	CHECK(kaimo_local_send_frame(closed.writer, KAIMO_LOCAL_OP_CONNECT,
				     KAIMO_LOCAL_KIND_REQUEST, 0, payload, 1) == -1);
	CHECK(errno == EPIPE);
	CHECK(kaimo_local_send_frame_until(closed.writer, KAIMO_LOCAL_OP_CONNECT,
					   KAIMO_LOCAL_KIND_REQUEST, 0, payload, 1,
					   &deadline) == -1);
}

void test_header_rejections()
{
	struct Case {
		std::vector<uint8_t> bytes;
		int error;
	};
	const uint8_t v = KAIMO_LOCAL_PROTOCOL_VERSION;
	const std::vector<Case> cases = {
		{header('X', v, 1, 2, 2, 0), EPROTO},
		{header('K', v + 1, 1, 2, 2, 0), EPROTO},
		{header('K', v, KAIMO_LOCAL_OP_MAX + 1, 2, 2, 0), EPROTO},
		{header('K', v, 1, 0, 2, 0), EPROTO},
		{header('K', v, 1, 3, 2, 0), EPROTO},
		{header('K', v, 1, 2, KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER + 1, 0), EPROTO},
		{header('K', v, 1, 2, 2, KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD + 1), EMSGSIZE},
		{header('K', v, 1, 1, 0, KAIMO_LOCAL_MAX_REQUEST_PAYLOAD + 1), EMSGSIZE},
	};
	for (const Case &item : cases) {
		kaimo_local_frame_header frame{};
		{
			Pair pair;
			pair.send(item.bytes);
			errno = 0;
			CHECK(kaimo_local_read_frame_header(pair.reader, &frame) == -1);
			CHECK(errno == item.error);
		}
		{
			Pair pair;
			pair.send(item.bytes);
			auto deadline = deadline_ms(1000);
			errno = 0;
			CHECK(kaimo_local_read_frame_header_until(pair.reader, &frame,
								  &deadline) == -1);
			CHECK(errno == item.error);
		}
	}

	// Accepted request header (the authd side).
	Pair pair;
	pair.send(header('K', v, KAIMO_LOCAL_OP_OPEN, 1, 0, KAIMO_LOCAL_MAX_REQUEST_PAYLOAD));
	kaimo_local_frame_header frame{};
	CHECK(kaimo_local_read_frame_header(pair.reader, &frame) == 0);
	CHECK(frame.operation == KAIMO_LOCAL_OP_OPEN);
	CHECK(frame.payload_length == KAIMO_LOCAL_MAX_REQUEST_PAYLOAD);
}

void test_eof_semantics()
{
	uint8_t buffer[12];
	{
		Pair pair;  // clean EOF before any byte: connection reset
		pair.hang_up();
		CHECK(kaimo_local_read_exact(pair.reader, buffer, sizeof(buffer)) == -1);
		CHECK(errno == ECONNRESET);
	}
	{
		Pair pair;  // EOF inside a frame: protocol violation
		pair.send({'K', 'A'});
		pair.hang_up();
		CHECK(kaimo_local_read_exact(pair.reader, buffer, sizeof(buffer)) == -1);
		CHECK(errno == EPROTO);
	}
	{
		Pair pair;
		pair.hang_up();
		auto deadline = deadline_ms(1000);
		CHECK(kaimo_local_read_exact_until(pair.reader, buffer, sizeof(buffer),
						   &deadline) == -1);
		CHECK(errno == ECONNRESET);
	}
	{
		Pair pair;
		pair.send({'K'});
		pair.hang_up();
		auto deadline = deadline_ms(1000);
		CHECK(kaimo_local_read_exact_until(pair.reader, buffer, sizeof(buffer),
						   &deadline) == -1);
		CHECK(errno == EPROTO);
	}
	{
		// Invalid descriptor: the plain readers and writers fail at once.
		CHECK(kaimo_local_read_exact(-1, buffer, 1) == -1);
		CHECK(kaimo_local_write_all(-1, buffer, 1) == -1);
	}
}

void test_deadlines()
{
	kaimo_local_deadline deadline{};
	CHECK(kaimo_local_deadline_init(nullptr, 10) == -1);
	CHECK(kaimo_local_deadline_init(&deadline, 0) == -1);
	CHECK(kaimo_local_deadline_remaining_ms(nullptr) == -1);

	// 1999 ms exercises the nanosecond carry into seconds.
	deadline = deadline_ms(1999);
	int remaining = kaimo_local_deadline_remaining_ms(&deadline);
	CHECK(remaining > 1900 && remaining <= 1999);

	deadline = deadline_ms(UINT32_MAX);
	CHECK(kaimo_local_deadline_remaining_ms(&deadline) == INT32_MAX);

	deadline = deadline_ms(1);
	usleep(5000);
	errno = 0;
	CHECK(kaimo_local_deadline_remaining_ms(&deadline) == 0);
	CHECK(errno == ETIMEDOUT);

	// wait_until: expired deadline, readable data, hang-up, invalid fd.
	Pair pair;
	CHECK(kaimo_local_wait_until(pair.reader, POLLIN, &deadline) == -1);
	auto fresh = deadline_ms(1000);
	pair.send({1});
	CHECK(kaimo_local_wait_until(pair.reader, POLLIN, &fresh) == 0);
	Pair hung;
	hung.hang_up();
	CHECK(kaimo_local_wait_until(hung.reader, POLLIN, &fresh) == 0);
	int fds[2];
	CHECK(pipe(fds) == 0);
	close(fds[0]);
	close(fds[1]);
	errno = 0;
	CHECK(kaimo_local_wait_until(fds[0], POLLIN, &fresh) == -1);
	CHECK(errno == EBADF);
	// Writable-only socket asked for readability times out.
	Pair idle;
	auto short_deadline = deadline_ms(20);
	errno = 0;
	CHECK(kaimo_local_wait_until(idle.reader, POLLIN, &short_deadline) == -1);
	CHECK(errno == ETIMEDOUT);
}

}  // namespace

int main()
{
	test_utf8_table();
	test_identity_validators();
	test_builder_and_reader_limits();
	test_frame_send_validation();
	test_header_rejections();
	test_eof_semantics();
	test_deadlines();
	std::cout << "local protocol edge tests passed" << std::endl;
	return 0;
}
