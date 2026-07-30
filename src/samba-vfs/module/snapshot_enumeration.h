#ifndef KAIMO_SNAPSHOT_ENUMERATION_H
#define KAIMO_SNAPSHOT_ENUMERATION_H

#include "local_protocol.h"

#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

/*
 * Validate the fixed Windows shadow-copy token representation emitted by the
 * bridge: @GMT-yyyy.MM.dd-HH.mm.ss. Calendar-range validation remains with the
 * managed DateTime formatter; this boundary validates the wire shape before a
 * token is copied into Samba's SHADOW_COPY_LABEL storage.
 */
static inline bool kaimo_snapshot_token_valid(
	const uint8_t *token, uint32_t length)
{
	static const char prefix[] = "@GMT-";

	if (token == NULL || length != KAIMO_LOCAL_SNAPSHOT_TOKEN_BYTES ||
	    memcmp(token, prefix, sizeof(prefix) - 1) != 0)
		return false;

	for (uint32_t i = (uint32_t)(sizeof(prefix) - 1); i < length; ++i) {
		if (i == 9 || i == 12 || i == 18 || i == 21) {
			if (token[i] != '.')
				return false;
		} else if (i == 15) {
			if (token[i] != '-')
				return false;
		} else if (token[i] < '0' || token[i] > '9') {
			return false;
		}
	}
	return true;
}

/*
 * Validate the complete SNAPSHOT_ENUMERATE response before allocation.
 *
 * The binary count is already overflow-safe, unlike the historical decimal
 * atoi parser. This helper adds the missing semantic bound and proves that the
 * advertised count exactly matches the received token records with no missing
 * or trailing payload.
 */
static inline bool kaimo_snapshot_enumeration_validate(
	const uint8_t *payload, size_t payload_length, uint32_t *count)
{
	struct kaimo_local_reader reader;
	uint32_t parsed_count;

	if (count == NULL) {
		errno = EINVAL;
		return false;
	}
	*count = 0;
	kaimo_local_reader_init(&reader, payload, payload_length);
	if (!kaimo_local_reader_u32(&reader, &parsed_count)) {
		errno = EPROTO;
		return false;
	}
	if (parsed_count > KAIMO_LOCAL_MAX_SNAPSHOT_LABELS) {
		errno = EMSGSIZE;
		return false;
	}

	/* Every valid record needs a 4-byte length plus the fixed 24-byte token.
	 * This precheck prevents a malicious count from driving a long parse loop. */
	if (parsed_count >
	    (reader.length - reader.offset) /
	    (4U + KAIMO_LOCAL_SNAPSHOT_TOKEN_BYTES)) {
		errno = EPROTO;
		return false;
	}

	for (uint32_t i = 0; i < parsed_count; ++i) {
		const uint8_t *token;
		uint32_t token_length;
		if (!kaimo_local_reader_string(
			    &reader, &token, &token_length) ||
		    !kaimo_snapshot_token_valid(token, token_length)) {
			errno = EPROTO;
			return false;
		}
	}
	if (!kaimo_local_reader_finished(&reader)) {
		errno = EPROTO;
		return false;
	}

	*count = parsed_count;
	return true;
}

#endif
