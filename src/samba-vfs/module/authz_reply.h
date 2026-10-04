/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_AUTHZ_REPLY_H
#define KAIMO_AUTHZ_REPLY_H

/*
 * Pure decision logic of the VFS module's authorization round trips: which
 * response frames are acceptable, how an authorization reply decodes, and what
 * the final allow/deny is for every verdict under the configured fail mode.
 * Kept free of Samba types so the complete policy is unit tested standalone
 * (tests/test-authz-reply.cpp); vfs_kaimo_bridge.c only adds logging.
 */

#include "local_protocol.h"
#include "recycle_move.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Samba 4.19.5 FILE_GENERIC_ALL after generic expansion. OPEN replies may
 * contain only these specific file/directory and standard access bits. */
#define KAIMO_SAMBA_SPECIFIC_ACCESS 0x001f01ffU

enum kaimo_authz_verdict {
	/* Protocol violation by the peer: always fails closed. */
	KAIMO_AUTHZ_MALFORMED = -2,
	/* Sidecar/bridge unavailable: follows the configured fail mode. */
	KAIMO_AUTHZ_UNAVAILABLE = -1,
	KAIMO_AUTHZ_DENY = 0,
	KAIMO_AUTHZ_ALLOW = 1
};

/*
 * A response header is acceptable when it is a RESPONSE for the requested
 * operation, or an operation-less infrastructure status (authd rejects some
 * requests before it has parsed the operation), and its payload fits the
 * caller's buffer.
 */
static inline bool kaimo_authz_response_header_acceptable(
	const struct kaimo_local_frame_header *response, uint8_t operation,
	size_t payload_capacity)
{
	if (response == NULL || response->kind != KAIMO_LOCAL_KIND_RESPONSE)
		return false;
	if (response->operation != operation &&
	    !(response->operation == KAIMO_LOCAL_OP_NONE &&
	      (response->status == KAIMO_LOCAL_STATUS_ERROR ||
	       response->status == KAIMO_LOCAL_STATUS_OVERLOADED ||
	       response->status == KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER)))
		return false;
	return response->payload_length <= payload_capacity;
}

/*
 * Decode one authorization reply. OPEN ALLOW replies carry the exact
 * normalized/attenuated access mask; DELETE_AUTH ALLOW replies carry the
 * recycle disposition and recycle root depth; every other ALLOW is empty.
 * Output parameters are written only for a well-formed ALLOW.
 */
static inline enum kaimo_authz_verdict kaimo_authz_decode_reply(
	uint8_t operation, uint8_t status, const uint8_t *payload,
	uint32_t payload_length, uint32_t *granted_access,
	bool *recycle_delete, unsigned int *recycle_root_depth)
{
	if (status == KAIMO_LOCAL_STATUS_ALLOW) {
		struct kaimo_local_reader reader;
		kaimo_local_reader_init(&reader, payload, payload_length);
		if (operation == KAIMO_LOCAL_OP_OPEN) {
			uint32_t parsed;
			if (granted_access == NULL ||
			    !kaimo_local_reader_u32(&reader, &parsed) ||
			    !kaimo_local_reader_finished(&reader) ||
			    (parsed & ~KAIMO_SAMBA_SPECIFIC_ACCESS) != 0)
				return KAIMO_AUTHZ_MALFORMED;
			*granted_access = parsed;
		} else if (operation == KAIMO_LOCAL_OP_DELETE_AUTH) {
			uint8_t parsed;
			uint8_t depth;
			if (recycle_delete == NULL ||
			    recycle_root_depth == NULL ||
			    !kaimo_local_reader_u8(&reader, &parsed) ||
			    parsed > 1 ||
			    !kaimo_local_reader_u8(&reader, &depth) ||
			    depth > KAIMO_RECYCLE_MAX_ROOT_DEPTH ||
			    !kaimo_local_reader_finished(&reader))
				return KAIMO_AUTHZ_MALFORMED;
			*recycle_delete = parsed != 0;
			*recycle_root_depth = depth;
		} else if (payload_length != 0) {
			return KAIMO_AUTHZ_MALFORMED;
		}
		return KAIMO_AUTHZ_ALLOW;
	}
	if (payload_length != 0)
		return KAIMO_AUTHZ_MALFORMED;
	if (status == KAIMO_LOCAL_STATUS_DENY)
		return KAIMO_AUTHZ_DENY;
	if (status == KAIMO_LOCAL_STATUS_ERROR ||
	    status == KAIMO_LOCAL_STATUS_OVERLOADED)
		return KAIMO_AUTHZ_UNAVAILABLE;
	/* UNAUTHORIZED_PEER means authd does not trust this smbd: never retry
	 * it as an outage. Any other status is not an authorization answer. */
	return KAIMO_AUTHZ_MALFORMED;
}

/*
 * Final decision. MALFORMED and DENY never allow. UNAVAILABLE follows the
 * configured fail mode, except for DELETE_AUTH: its reply carries the
 * permanent-vs-recycle disposition, and guessing "permanent" during an outage
 * could irreversibly bypass an enabled recycle bin.
 */
static inline bool kaimo_authz_permits(enum kaimo_authz_verdict verdict,
				       uint8_t operation, bool fail_open)
{
	switch (verdict) {
	case KAIMO_AUTHZ_ALLOW:
		return true;
	case KAIMO_AUTHZ_UNAVAILABLE:
		return fail_open && operation != KAIMO_LOCAL_OP_DELETE_AUTH;
	case KAIMO_AUTHZ_DENY:
	case KAIMO_AUTHZ_MALFORMED:
	default:
		return false;
	}
}

/* MS-SMB2 CreateDisposition values that truncate an existing file, and the
 * right they consume (vfs_kaimo_bridge.c asserts parity with Samba). */
#define KAIMO_FILE_SUPERSEDE 0U
#define KAIMO_FILE_OVERWRITE 4U
#define KAIMO_FILE_OVERWRITE_IF 5U
#define KAIMO_FILE_WRITE_DATA 0x00000002U

/*
 * Samba truncates an existing file for SUPERSEDE/OVERWRITE(_IF) inside the
 * open itself, judged only by POSIX permissions, before the handle's access
 * mask is ever consulted. The granted mask must therefore carry
 * FILE_WRITE_DATA for these dispositions, or a read-only grant destroys data.
 */
static inline bool kaimo_authz_open_disposition_permitted(
	uint32_t create_disposition, uint32_t granted_access)
{
	switch (create_disposition) {
	case KAIMO_FILE_SUPERSEDE:
	case KAIMO_FILE_OVERWRITE:
	case KAIMO_FILE_OVERWRITE_IF:
		return (granted_access & KAIMO_FILE_WRITE_DATA) != 0;
	default:
		return true;
	}
}

/* KAIMO_AUTHZ_FAILOPEN=1 (only the leading '1' is significant). */
static inline bool kaimo_authz_failopen_configured(const char *value)
{
	return value != NULL && value[0] == '1';
}

#endif
