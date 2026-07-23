#ifndef KAIMO_LOCAL_PROTOCOL_H
#define KAIMO_LOCAL_PROTOCOL_H

/*
 * Versioned binary protocol used only between vfs_kaimo_bridge and kaimo_authd.
 *
 * Wire header (12 bytes):
 *   0..3   magic "KAIM"
 *   4      protocol version
 *   5      operation enum
 *   6      message kind (request/response)
 *   7      response status (zero on requests)
 *   8..11  payload length, unsigned big endian
 *
 * Payload integers are big endian. Strings are encoded as a uint32 byte length
 * followed by exactly that many bytes. They are not NUL terminated on the wire.
 */

#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>

#define KAIMO_LOCAL_PROTOCOL_VERSION 1U
#define KAIMO_LOCAL_HEADER_SIZE 12U
#define KAIMO_LOCAL_MAX_REQUEST_PAYLOAD 8192U
#define KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD 65536U
#define KAIMO_LOCAL_MAX_STRING_BYTES 8191U

enum kaimo_local_operation {
	KAIMO_LOCAL_OP_NONE = 0,
	KAIMO_LOCAL_OP_CONNECT = 1,
	KAIMO_LOCAL_OP_OPEN = 2,
	KAIMO_LOCAL_OP_DELETE_AUTH = 3,
	KAIMO_LOCAL_OP_RENAME_AUTH = 4,
	KAIMO_LOCAL_OP_CLOSE = 5,
	KAIMO_LOCAL_OP_MKDIR = 6,
	KAIMO_LOCAL_OP_DELETE = 7,
	KAIMO_LOCAL_OP_RENAME = 8,
	KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE = 9,
	KAIMO_LOCAL_OP_SNAPSHOT_RESOLVE = 10,
	KAIMO_LOCAL_OP_MAX = KAIMO_LOCAL_OP_SNAPSHOT_RESOLVE
};

enum kaimo_local_message_kind {
	KAIMO_LOCAL_KIND_REQUEST = 1,
	KAIMO_LOCAL_KIND_RESPONSE = 2
};

enum kaimo_local_status {
	KAIMO_LOCAL_STATUS_NONE = 0,
	KAIMO_LOCAL_STATUS_OK = 1,
	KAIMO_LOCAL_STATUS_ALLOW = 2,
	KAIMO_LOCAL_STATUS_DENY = 3,
	KAIMO_LOCAL_STATUS_NOT_FOUND = 4,
	KAIMO_LOCAL_STATUS_ERROR = 5,
	KAIMO_LOCAL_STATUS_OVERLOADED = 6
};

struct kaimo_local_frame_header {
	uint8_t operation;
	uint8_t kind;
	uint8_t status;
	uint32_t payload_length;
};

struct kaimo_local_builder {
	uint8_t *data;
	size_t capacity;
	size_t length;
	bool valid;
};

struct kaimo_local_reader {
	const uint8_t *data;
	size_t length;
	size_t offset;
	bool valid;
};

static inline void kaimo_local_put_be32(uint8_t *destination, uint32_t value)
{
	destination[0] = (uint8_t)(value >> 24);
	destination[1] = (uint8_t)(value >> 16);
	destination[2] = (uint8_t)(value >> 8);
	destination[3] = (uint8_t)value;
}

static inline uint32_t kaimo_local_get_be32(const uint8_t *source)
{
	return ((uint32_t)source[0] << 24) |
	       ((uint32_t)source[1] << 16) |
	       ((uint32_t)source[2] << 8) |
	       (uint32_t)source[3];
}

static inline void kaimo_local_put_be64(uint8_t *destination, uint64_t value)
{
	kaimo_local_put_be32(destination, (uint32_t)(value >> 32));
	kaimo_local_put_be32(destination + 4, (uint32_t)value);
}

static inline uint64_t kaimo_local_get_be64(const uint8_t *source)
{
	return ((uint64_t)kaimo_local_get_be32(source) << 32) |
	       (uint64_t)kaimo_local_get_be32(source + 4);
}

static inline void kaimo_local_builder_init(struct kaimo_local_builder *builder,
					    uint8_t *storage,
					    size_t capacity)
{
	builder->data = storage;
	builder->capacity = capacity;
	builder->length = 0;
	builder->valid = storage != NULL;
}

static inline bool kaimo_local_builder_reserve(
	struct kaimo_local_builder *builder, size_t length)
{
	if (!builder->valid || length > builder->capacity - builder->length) {
		builder->valid = false;
		errno = EMSGSIZE;
		return false;
	}
	return true;
}

static inline bool kaimo_local_builder_u8(struct kaimo_local_builder *builder,
					 uint8_t value)
{
	if (!kaimo_local_builder_reserve(builder, 1))
		return false;
	builder->data[builder->length++] = value;
	return true;
}

static inline bool kaimo_local_builder_u32(struct kaimo_local_builder *builder,
					  uint32_t value)
{
	if (!kaimo_local_builder_reserve(builder, 4))
		return false;
	kaimo_local_put_be32(builder->data + builder->length, value);
	builder->length += 4;
	return true;
}

static inline bool kaimo_local_builder_u64(struct kaimo_local_builder *builder,
					  uint64_t value)
{
	if (!kaimo_local_builder_reserve(builder, 8))
		return false;
	kaimo_local_put_be64(builder->data + builder->length, value);
	builder->length += 8;
	return true;
}

static inline bool kaimo_local_builder_string(
	struct kaimo_local_builder *builder, const char *value)
{
	size_t length = value != NULL ? strlen(value) : 0;
	if (length > KAIMO_LOCAL_MAX_STRING_BYTES ||
	    length > UINT32_MAX ||
	    !kaimo_local_builder_reserve(builder, 4 + length)) {
		builder->valid = false;
		errno = EMSGSIZE;
		return false;
	}
	kaimo_local_put_be32(builder->data + builder->length, (uint32_t)length);
	builder->length += 4;
	if (length != 0) {
		memcpy(builder->data + builder->length, value, length);
		builder->length += length;
	}
	return true;
}

static inline void kaimo_local_reader_init(struct kaimo_local_reader *reader,
					   const uint8_t *data,
					   size_t length)
{
	reader->data = data;
	reader->length = length;
	reader->offset = 0;
	reader->valid = data != NULL || length == 0;
}

static inline bool kaimo_local_reader_take(struct kaimo_local_reader *reader,
					  size_t length,
					  const uint8_t **value)
{
	if (!reader->valid || length > reader->length - reader->offset) {
		reader->valid = false;
		errno = EPROTO;
		return false;
	}
	*value = reader->data + reader->offset;
	reader->offset += length;
	return true;
}

static inline bool kaimo_local_reader_u8(struct kaimo_local_reader *reader,
					uint8_t *value)
{
	const uint8_t *bytes;
	if (!kaimo_local_reader_take(reader, 1, &bytes))
		return false;
	*value = bytes[0];
	return true;
}

static inline bool kaimo_local_reader_u32(struct kaimo_local_reader *reader,
					 uint32_t *value)
{
	const uint8_t *bytes;
	if (!kaimo_local_reader_take(reader, 4, &bytes))
		return false;
	*value = kaimo_local_get_be32(bytes);
	return true;
}

static inline bool kaimo_local_reader_u64(struct kaimo_local_reader *reader,
					 uint64_t *value)
{
	const uint8_t *bytes;
	if (!kaimo_local_reader_take(reader, 8, &bytes))
		return false;
	*value = kaimo_local_get_be64(bytes);
	return true;
}

static inline bool kaimo_local_valid_utf8(const uint8_t *value, size_t length)
{
	size_t offset = 0;
	while (offset < length) {
		uint8_t first = value[offset++];
		if (first <= 0x7f)
			continue;
		if (first >= 0xc2 && first <= 0xdf) {
			if (offset >= length ||
			    (value[offset++] & 0xc0) != 0x80)
				return false;
			continue;
		}
		if (first >= 0xe0 && first <= 0xef) {
			if (offset + 1 >= length)
				return false;
			uint8_t second = value[offset++];
			uint8_t third = value[offset++];
			if ((third & 0xc0) != 0x80 ||
			    (first == 0xe0 && (second < 0xa0 || second > 0xbf)) ||
			    (first == 0xed && (second < 0x80 || second > 0x9f)) ||
			    ((first != 0xe0 && first != 0xed) &&
			     (second & 0xc0) != 0x80))
				return false;
			continue;
		}
		if (first >= 0xf0 && first <= 0xf4) {
			if (offset + 2 >= length)
				return false;
			uint8_t second = value[offset++];
			uint8_t third = value[offset++];
			uint8_t fourth = value[offset++];
			if ((third & 0xc0) != 0x80 ||
			    (fourth & 0xc0) != 0x80 ||
			    (first == 0xf0 && (second < 0x90 || second > 0xbf)) ||
			    (first == 0xf4 && (second < 0x80 || second > 0x8f)) ||
			    ((first != 0xf0 && first != 0xf4) &&
			     (second & 0xc0) != 0x80))
				return false;
			continue;
		}
		return false;
	}
	return true;
}

static inline bool kaimo_local_reader_string(
	struct kaimo_local_reader *reader, const uint8_t **value, uint32_t *length)
{
	uint32_t string_length;
	const uint8_t *bytes;
	if (!kaimo_local_reader_u32(reader, &string_length) ||
	    string_length > KAIMO_LOCAL_MAX_STRING_BYTES ||
	    !kaimo_local_reader_take(reader, string_length, &bytes) ||
	    memchr(bytes, '\0', string_length) != NULL ||
	    !kaimo_local_valid_utf8(bytes, string_length)) {
		reader->valid = false;
		errno = EPROTO;
		return false;
	}
	*value = bytes;
	*length = string_length;
	return true;
}

static inline bool kaimo_local_reader_finished(
	const struct kaimo_local_reader *reader)
{
	return reader->valid && reader->offset == reader->length;
}

static inline int kaimo_local_write_all(int fd, const void *data, size_t length)
{
	const uint8_t *cursor = (const uint8_t *)data;
	while (length != 0) {
		ssize_t written = send(fd, cursor, length, MSG_NOSIGNAL);
		if (written < 0) {
			if (errno == EINTR)
				continue;
			return -1;
		}
		if (written == 0) {
			errno = EPIPE;
			return -1;
		}
		cursor += (size_t)written;
		length -= (size_t)written;
	}
	return 0;
}

static inline int kaimo_local_read_exact(int fd, void *data, size_t length)
{
	uint8_t *cursor = (uint8_t *)data;
	size_t received = 0;
	while (received != length) {
		ssize_t result = recv(fd, cursor + received, length - received, 0);
		if (result < 0) {
			if (errno == EINTR)
				continue;
			return -1;
		}
		if (result == 0) {
			errno = received == 0 ? ECONNRESET : EPROTO;
			return -1;
		}
		received += (size_t)result;
	}
	return 0;
}

static inline int kaimo_local_send_frame(int fd, uint8_t operation,
					uint8_t kind, uint8_t status,
					const uint8_t *payload,
					size_t payload_length)
{
	uint8_t header[KAIMO_LOCAL_HEADER_SIZE] = {
		'K', 'A', 'I', 'M',
		KAIMO_LOCAL_PROTOCOL_VERSION,
		operation,
		kind,
		status,
		0, 0, 0, 0
	};
	size_t maximum = kind == KAIMO_LOCAL_KIND_REQUEST
		? KAIMO_LOCAL_MAX_REQUEST_PAYLOAD
		: KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD;

	if ((kind != KAIMO_LOCAL_KIND_REQUEST &&
	     kind != KAIMO_LOCAL_KIND_RESPONSE) ||
	    operation > KAIMO_LOCAL_OP_MAX ||
	    payload_length > maximum ||
	    payload_length > UINT32_MAX ||
	    (payload_length != 0 && payload == NULL)) {
		errno = EMSGSIZE;
		return -1;
	}
	kaimo_local_put_be32(header + 8, (uint32_t)payload_length);
	if (kaimo_local_write_all(fd, header, sizeof(header)) != 0)
		return -1;
	return kaimo_local_write_all(fd, payload, payload_length);
}

static inline int kaimo_local_read_frame_header(
	int fd, struct kaimo_local_frame_header *frame)
{
	uint8_t header[KAIMO_LOCAL_HEADER_SIZE];
	if (kaimo_local_read_exact(fd, header, sizeof(header)) != 0)
		return -1;

	if (memcmp(header, "KAIM", 4) != 0 ||
	    header[4] != KAIMO_LOCAL_PROTOCOL_VERSION ||
	    header[5] > KAIMO_LOCAL_OP_MAX ||
	    (header[6] != KAIMO_LOCAL_KIND_REQUEST &&
	     header[6] != KAIMO_LOCAL_KIND_RESPONSE) ||
	    header[7] > KAIMO_LOCAL_STATUS_OVERLOADED) {
		errno = EPROTO;
		return -1;
	}

	frame->operation = header[5];
	frame->kind = header[6];
	frame->status = header[7];
	frame->payload_length = kaimo_local_get_be32(header + 8);
	size_t maximum = frame->kind == KAIMO_LOCAL_KIND_REQUEST
		? KAIMO_LOCAL_MAX_REQUEST_PAYLOAD
		: KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD;
	if (frame->payload_length > maximum) {
		errno = EMSGSIZE;
		return -1;
	}
	return 0;
}

#endif
