#include "authd_client.h"

#include "kaimo_check.h"

#include <cerrno>
#include <cstring>
#include <cstdint>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <string>
#include <sys/socket.h>
#include <sys/un.h>
#include <thread>
#include <unistd.h>
#include <vector>

namespace {

/* One-shot authd stand-in: accepts a single connection, reads one complete
 * request frame and hands the socket to `reply` for the scripted answer. */
class FakeAuthd {
public:
	explicit FakeAuthd(std::function<void(int)> reply)
	{
		char directory[] = "/tmp/kaimo-authd-client-XXXXXX";
		CHECK(mkdtemp(directory) != nullptr);
		directory_ = directory;
		path_ = directory_ + "/authz.sock";

		listener_ = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
		CHECK(listener_ >= 0);
		sockaddr_un address{};
		address.sun_family = AF_UNIX;
		CHECK(path_.size() < sizeof(address.sun_path));
		std::memcpy(address.sun_path, path_.c_str(), path_.size() + 1);
		CHECK(bind(listener_, reinterpret_cast<sockaddr *>(&address),
			   sizeof(address)) == 0);
		CHECK(listen(listener_, 1) == 0);

		server_ = std::thread([this, reply] {
			int connection = accept(listener_, nullptr, nullptr);
			CHECK(connection >= 0);
			kaimo_local_frame_header request{};
			CHECK(kaimo_local_read_frame_header(connection, &request) == 0);
			request_operation_ = request.operation;
			request_payload_.resize(request.payload_length);
			if (request.payload_length != 0)
				CHECK(kaimo_local_read_exact(
					      connection, request_payload_.data(),
					      request.payload_length) == 0);
			reply(connection);
			close(connection);
		});
	}

	~FakeAuthd()
	{
		server_.join();
		close(listener_);
		unlink(path_.c_str());
		rmdir(directory_.c_str());
	}

	const char *path() const { return path_.c_str(); }
	uint8_t request_operation() const { return request_operation_; }
	const std::vector<uint8_t> &request_payload() const { return request_payload_; }

private:
	std::string directory_;
	std::string path_;
	int listener_ = -1;
	std::thread server_;
	uint8_t request_operation_ = 0;
	std::vector<uint8_t> request_payload_;
};

std::function<void(int)> respond(uint8_t operation, uint8_t status,
				 std::vector<uint8_t> payload)
{
	return [=](int fd) {
		CHECK(kaimo_local_send_frame(
			      fd, operation, KAIMO_LOCAL_KIND_RESPONSE, status,
			      payload.empty() ? nullptr : payload.data(),
			      payload.size()) == 0);
	};
}

std::function<void(int)> raw(std::vector<uint8_t> bytes)
{
	return [=](int fd) {
		CHECK(kaimo_local_write_all(fd, bytes.data(), bytes.size()) == 0);
	};
}

struct Result {
	int code;
	int error;
	kaimo_local_frame_header response;
	std::vector<uint8_t> payload;
};

Result roundtrip(const char *socket_path, uint8_t operation,
		 size_t payload_capacity, uint32_t timeout_ms = 2000)
{
	kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, "alice");
	kaimo_local_builder_u8(&request.builder, 1);

	Result result{};
	result.payload.assign(payload_capacity, 0xee);
	errno = 0;
	result.code = kaimo_authd_roundtrip(
		socket_path, operation, &request, &result.response,
		payload_capacity != 0 ? result.payload.data() : nullptr,
		payload_capacity, timeout_ms);
	result.error = errno;
	return result;
}

void test_request_init_and_happy_path()
{
	FakeAuthd authd(respond(KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_STATUS_ALLOW,
				{0x00, 0x12, 0x00, 0x89}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_OPEN, 4);

	CHECK(result.code == 0);
	CHECK(result.response.operation == KAIMO_LOCAL_OP_OPEN);
	CHECK(result.response.kind == KAIMO_LOCAL_KIND_RESPONSE);
	CHECK(result.response.status == KAIMO_LOCAL_STATUS_ALLOW);
	CHECK(result.response.payload_length == 4);
	CHECK((result.payload == std::vector<uint8_t>{0x00, 0x12, 0x00, 0x89}));

	CHECK(authd.request_operation() == KAIMO_LOCAL_OP_OPEN);
	/* u32 length + "alice" + u8 flag, exactly as built. */
	CHECK((authd.request_payload() ==
	       std::vector<uint8_t>{0, 0, 0, 5, 'a', 'l', 'i', 'c', 'e', 1}));
}

void test_empty_reply_without_payload_buffer()
{
	FakeAuthd authd(respond(KAIMO_LOCAL_OP_CLOSE, KAIMO_LOCAL_STATUS_OK, {}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_CLOSE, 0);
	CHECK(result.code == 0);
	CHECK(result.response.status == KAIMO_LOCAL_STATUS_OK);
	CHECK(result.response.payload_length == 0);
}

void test_generic_error_reply_is_transport_ok()
{
	/* authd answers malformed/overloaded requests with operation NONE. */
	FakeAuthd authd(respond(KAIMO_LOCAL_OP_NONE,
				KAIMO_LOCAL_STATUS_OVERLOADED, {}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == 0);
	CHECK(result.response.status == KAIMO_LOCAL_STATUS_OVERLOADED);
}

void test_mismatched_operation_is_malformed()
{
	FakeAuthd authd(respond(KAIMO_LOCAL_OP_CONNECT,
				KAIMO_LOCAL_STATUS_ALLOW, {}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == -2);
	CHECK(result.error == EPROTO);
}

void test_payload_above_capacity_is_malformed()
{
	FakeAuthd authd(respond(KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_STATUS_ALLOW,
				{1, 2, 3, 4, 5}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == -2);
	CHECK(result.error == EPROTO);
	/* Nothing may be read into caller storage before validation. */
	CHECK((result.payload == std::vector<uint8_t>(4, 0xee)));
}

void test_bad_header_is_malformed()
{
	FakeAuthd authd(raw({'N', 'O', 'P', 'E', KAIMO_LOCAL_PROTOCOL_VERSION,
			     KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_KIND_RESPONSE,
			     KAIMO_LOCAL_STATUS_ALLOW, 0, 0, 0, 0}));
	Result result = roundtrip(authd.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == -2);
}

void test_truncated_payload()
{
	/* Header promises 4 bytes; a partial payload is a framing violation. */
	FakeAuthd partial(raw({'K', 'A', 'I', 'M', KAIMO_LOCAL_PROTOCOL_VERSION,
			       KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_KIND_RESPONSE,
			       KAIMO_LOCAL_STATUS_ALLOW, 0, 0, 0, 4, 0xaa}));
	Result result = roundtrip(partial.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == -2);
	CHECK(result.error == EPROTO);
}

void test_peer_closes_before_payload()
{
	FakeAuthd closed(raw({'K', 'A', 'I', 'M', KAIMO_LOCAL_PROTOCOL_VERSION,
			      KAIMO_LOCAL_OP_OPEN, KAIMO_LOCAL_KIND_RESPONSE,
			      KAIMO_LOCAL_STATUS_ALLOW, 0, 0, 0, 4}));
	Result result = roundtrip(closed.path(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(result.code == -1);
	CHECK(result.error == ECONNRESET);
}

void test_silent_peer_times_out()
{
	FakeAuthd silent([](int fd) {
		char byte;
		/* Hold the connection until the client gives up and closes. */
		while (recv(fd, &byte, 1, 0) > 0) {
		}
	});
	Result result = roundtrip(silent.path(), KAIMO_LOCAL_OP_OPEN, 4, 50);
	CHECK(result.code == -1);
	CHECK(result.error == ETIMEDOUT);
}

void test_connect_failures()
{
	Result missing = roundtrip("/tmp/kaimo-authd-client-missing/authz.sock",
				   KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(missing.code == -1);
	CHECK(missing.error == ENOENT);

	std::string too_long = "/tmp/" + std::string(sizeof(sockaddr_un{}.sun_path), 'x');
	Result long_path = roundtrip(too_long.c_str(), KAIMO_LOCAL_OP_OPEN, 4);
	CHECK(long_path.code == -1);
	CHECK(long_path.error == ENAMETOOLONG);

	/* A zero budget is rejected before any socket is created. */
	Result no_budget = roundtrip("/tmp/unused.sock", KAIMO_LOCAL_OP_OPEN, 4, 0);
	CHECK(no_budget.code == -1);
}

void test_oversized_request_is_not_sent()
{
	kaimo_local_request request;
	kaimo_request_init(&request);
	std::string huge(KAIMO_LOCAL_MAX_REQUEST_PAYLOAD, 'x');
	kaimo_local_builder_string(&request.builder, huge.c_str());
	CHECK(!request.builder.valid);
}

}  // namespace

int main()
{
	test_request_init_and_happy_path();
	test_empty_reply_without_payload_buffer();
	test_generic_error_reply_is_transport_ok();
	test_mismatched_operation_is_malformed();
	test_payload_above_capacity_is_malformed();
	test_bad_header_is_malformed();
	test_truncated_payload();
	test_peer_closes_before_payload();
	test_silent_peer_times_out();
	test_connect_failures();
	test_oversized_request_is_not_sent();
	std::cout << "authd client tests passed" << std::endl;
	return 0;
}
