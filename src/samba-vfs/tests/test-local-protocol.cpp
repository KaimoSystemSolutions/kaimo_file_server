#include "local_protocol.h"

#include <array>
#include <cassert>
#include <cerrno>
#include <chrono>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include <sys/socket.h>
#include <unistd.h>

static void test_binary_fields()
{
	std::array<uint8_t, 128> storage{};
	kaimo_local_builder builder;
	kaimo_local_builder_init(&builder, storage.data(), storage.size());
	assert(kaimo_local_builder_string(&builder, "user\tname"));
	assert(kaimo_local_builder_string(&builder, "dir/line\nname"));
	assert(kaimo_local_builder_u32(&builder, 0x12345678U));
	assert(kaimo_local_builder_u64(&builder, 0x0102030405060708ULL));

	kaimo_local_reader reader;
	kaimo_local_reader_init(&reader, storage.data(), builder.length);
	const uint8_t *value = nullptr;
	uint32_t length = 0;
	uint32_t value32 = 0;
	uint64_t value64 = 0;
	assert(kaimo_local_reader_string(&reader, &value, &length));
	assert(std::string(reinterpret_cast<const char *>(value), length) ==
	       "user\tname");
	assert(kaimo_local_reader_string(&reader, &value, &length));
	assert(std::string(reinterpret_cast<const char *>(value), length) ==
	       "dir/line\nname");
	assert(kaimo_local_reader_u32(&reader, &value32));
	assert(value32 == 0x12345678U);
	assert(kaimo_local_reader_u64(&reader, &value64));
	assert(value64 == 0x0102030405060708ULL);
	assert(kaimo_local_reader_finished(&reader));
}

static void test_identity_context_bounds()
{
	assert(kaimo_local_valid_username("a"));
	assert(kaimo_local_valid_username(
		"abcdefghijklmnopqrstuvwxyz123456"));
	assert(!kaimo_local_valid_username(
		"abcdefghijklmnopqrstuvwxyz1234567"));
	assert(!kaimo_local_valid_username(".alice"));
	assert(!kaimo_local_valid_username("alice\xC3\xA4"));
	assert(!kaimo_local_valid_username("alice/name"));

	assert(kaimo_local_valid_share("share"));
	assert(kaimo_local_valid_share(
		"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"));
	assert(!kaimo_local_valid_share(
		"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_."));
	assert(!kaimo_local_valid_share(".hidden"));
	assert(!kaimo_local_valid_share("IPC$"));
	assert(!kaimo_local_valid_share("share/name"));
}

static void test_fragmented_frame_read()
{
	int sockets[2];
	assert(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
	std::array<uint8_t, KAIMO_LOCAL_HEADER_SIZE + 7> frame{
		'K', 'A', 'I', 'M', KAIMO_LOCAL_PROTOCOL_VERSION,
		KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_KIND_REQUEST,
		KAIMO_LOCAL_STATUS_NONE, 0, 0, 0, 7,
		1, 2, 3, 4, 5, 6, 7
	};
	std::thread writer([&] {
		for (uint8_t byte : frame)
			assert(send(sockets[0], &byte, 1, MSG_NOSIGNAL) == 1);
		close(sockets[0]);
	});

	kaimo_local_frame_header header{};
	assert(kaimo_local_read_frame_header(sockets[1], &header) == 0);
	assert(header.operation == KAIMO_LOCAL_OP_OPEN);
	assert(header.kind == KAIMO_LOCAL_KIND_REQUEST);
	assert(header.payload_length == 7);
	std::array<uint8_t, 7> payload{};
	assert(kaimo_local_read_exact(sockets[1], payload.data(), payload.size()) == 0);
	assert(payload[0] == 1 && payload[6] == 7);
	close(sockets[1]);
	writer.join();
}

static void test_write_all_and_frame_bounds()
{
	int sockets[2];
	assert(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
	int send_buffer = 1024;
	assert(setsockopt(sockets[0], SOL_SOCKET, SO_SNDBUF, &send_buffer,
			  sizeof(send_buffer)) == 0);
	std::vector<uint8_t> payload(KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD, 0x5a);
	std::thread reader([&] {
		kaimo_local_frame_header header{};
		assert(kaimo_local_read_frame_header(sockets[1], &header) == 0);
		assert(header.operation == KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE);
		assert(header.kind == KAIMO_LOCAL_KIND_RESPONSE);
		assert(header.status == KAIMO_LOCAL_STATUS_OK);
		assert(header.payload_length == payload.size());
		std::vector<uint8_t> received(payload.size());
		assert(kaimo_local_read_exact(sockets[1], received.data(),
					      received.size()) == 0);
		assert(received == payload);
		close(sockets[1]);
	});
	assert(kaimo_local_send_frame(
		       sockets[0], KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE,
		       KAIMO_LOCAL_KIND_RESPONSE, KAIMO_LOCAL_STATUS_OK,
		       payload.data(), payload.size()) == 0);
	close(sockets[0]);
	reader.join();

	errno = 0;
	assert(kaimo_local_send_frame(
		       -1, KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_KIND_REQUEST,
		       KAIMO_LOCAL_STATUS_NONE, payload.data(),
		       KAIMO_LOCAL_MAX_REQUEST_PAYLOAD + 1) == -1);
	assert(errno == EMSGSIZE);
}

static void test_rejects_embedded_nul_and_trailing_fields()
{
	const std::array<uint8_t, 7> encoded{
		0, 0, 0, 3, 'a', 0, 'b'
	};
	kaimo_local_reader reader;
	kaimo_local_reader_init(&reader, encoded.data(), encoded.size());
	const uint8_t *value = nullptr;
	uint32_t length = 0;
	assert(!kaimo_local_reader_string(&reader, &value, &length));
	assert(!kaimo_local_reader_finished(&reader));

	const std::array<uint8_t, 6> invalid_utf8{
		0, 0, 0, 2, 0xc0, 0xaf
	};
	kaimo_local_reader_init(&reader, invalid_utf8.data(),
				invalid_utf8.size());
	assert(!kaimo_local_reader_string(&reader, &value, &length));
}

static void test_absolute_receive_deadline()
{
	int sockets[2];
	assert(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
	std::thread writer([&] {
		const std::array<uint8_t, 4> bytes{1, 2, 3, 4};
		for (uint8_t byte : bytes) {
			std::this_thread::sleep_for(std::chrono::milliseconds(35));
			if (send(sockets[0], &byte, 1, MSG_NOSIGNAL) != 1)
				break;
		}
		close(sockets[0]);
	});

	struct kaimo_local_deadline deadline;
	assert(kaimo_local_deadline_init(&deadline, 80) == 0);
	std::array<uint8_t, 4> received{};
	auto started = std::chrono::steady_clock::now();
	errno = 0;
	assert(kaimo_local_read_exact_until(
		       sockets[1], received.data(), received.size(),
		       &deadline) == -1);
	auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
		std::chrono::steady_clock::now() - started);
	assert(errno == ETIMEDOUT);
	assert(elapsed.count() >= 50);
	assert(elapsed.count() < 250);
	close(sockets[1]);
	writer.join();
}

static void test_absolute_send_deadline()
{
	int sockets[2];
	assert(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
	int send_buffer = 1024;
	assert(setsockopt(sockets[0], SOL_SOCKET, SO_SNDBUF, &send_buffer,
			  sizeof(send_buffer)) == 0);
	std::vector<uint8_t> payload(2 * 1024 * 1024, 0x5a);
	struct kaimo_local_deadline deadline;
	assert(kaimo_local_deadline_init(&deadline, 80) == 0);
	auto started = std::chrono::steady_clock::now();
	errno = 0;
	assert(kaimo_local_write_all_until(
		       sockets[0], payload.data(), payload.size(),
		       &deadline) == -1);
	auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
		std::chrono::steady_clock::now() - started);
	assert(errno == ETIMEDOUT);
	assert(elapsed.count() >= 50);
	assert(elapsed.count() < 250);
	close(sockets[0]);
	close(sockets[1]);
}

int main()
{
	test_binary_fields();
	test_identity_context_bounds();
	test_fragmented_frame_read();
	test_write_all_and_frame_bounds();
	test_rejects_embedded_nul_and_trailing_fields();
	test_absolute_receive_deadline();
	test_absolute_send_deadline();
	return 0;
}
