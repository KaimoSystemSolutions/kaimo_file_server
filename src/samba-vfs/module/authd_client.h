/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_AUTHD_CLIENT_H
#define KAIMO_AUTHD_CLIENT_H

/*
 * Client side of the local kaimo_authd socket used by vfs_kaimo_bridge.c:
 * a bounded request buffer and one deadline-limited request/response
 * roundtrip per call. Free of Samba types; unit tested in
 * tests/test-authd-client.cpp against a scripted fake authd.
 */

#include "authz_reply.h"
#include "local_protocol.h"

#include <errno.h>
#include <poll.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

struct kaimo_local_request {
	uint8_t payload[KAIMO_LOCAL_MAX_REQUEST_PAYLOAD];
	struct kaimo_local_builder builder;
};

static inline void kaimo_request_init(struct kaimo_local_request *request)
{
	kaimo_local_builder_init(&request->builder, request->payload,
				 sizeof(request->payload));
}

static inline int kaimo_authd_connect(
	const char *sock_path, const struct kaimo_local_deadline *deadline)
{
	int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
	if (fd < 0)
		return -1;

	struct sockaddr_un addr;
	memset(&addr, 0, sizeof(addr));
	addr.sun_family = AF_UNIX;
	if (strlcpy(addr.sun_path, sock_path, sizeof(addr.sun_path)) >=
	    sizeof(addr.sun_path)) {
		close(fd);
		errno = ENAMETOOLONG;
		return -1;
	}
	if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0)
		return fd;
	if (errno != EINPROGRESS && errno != EAGAIN) {
		close(fd);
		return -1;
	}
	if (kaimo_local_wait_until(fd, POLLOUT, deadline) != 0) {
		close(fd);
		return -1;
	}
	int socket_error = 0;
	socklen_t socket_error_size = sizeof(socket_error);
	if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &socket_error,
		       &socket_error_size) != 0) {
		close(fd);
		return -1;
	}
	if (socket_error != 0) {
		close(fd);
		errno = socket_error;
		return -1;
	}
	return fd;
}

/* Write one complete request frame and read one complete response frame.
 * Header lengths are validated before response bytes are read into caller
 * storage. Return 0 on success, -1 on transport failure, -2 on malformed wire
 * data. */
static inline int kaimo_authd_roundtrip(
	const char *sock_path, uint8_t operation,
	const struct kaimo_local_request *request,
	struct kaimo_local_frame_header *response,
	uint8_t *payload, size_t payload_capacity, uint32_t timeout_ms)
{
	struct kaimo_local_deadline deadline;
	if (kaimo_local_deadline_init(&deadline, timeout_ms) != 0)
		return -1;
	int fd = kaimo_authd_connect(sock_path, &deadline);
	if (fd < 0)
		return -1;

	if (kaimo_local_send_frame_until(
		    fd, operation, KAIMO_LOCAL_KIND_REQUEST,
		    KAIMO_LOCAL_STATUS_NONE, request->payload,
		    request->builder.length, &deadline) != 0) {
		close(fd);
		return -1;
	}

	if (kaimo_local_read_frame_header_until(
		    fd, response, &deadline) != 0) {
		int saved_errno = errno;
		close(fd);
		return (saved_errno == EPROTO || saved_errno == EMSGSIZE) ? -2 : -1;
	}
	if (!kaimo_authz_response_header_acceptable(
		    response, operation, payload_capacity)) {
		close(fd);
		errno = EPROTO;
		return -2;
	}
	if (response->payload_length != 0 &&
	    kaimo_local_read_exact_until(
		    fd, payload, response->payload_length, &deadline) != 0) {
		int saved_errno = errno;
		close(fd);
		return saved_errno == EPROTO ? -2 : -1;
	}
	close(fd);
	return 0;
}

#endif
