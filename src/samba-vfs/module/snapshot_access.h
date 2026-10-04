/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_SNAPSHOT_ACCESS_H
#define KAIMO_SNAPSHOT_ACCESS_H

/*
 * Pure validation for the @GMT snapshot data path of vfs_kaimo_bridge.c:
 * the SNAPSHOT_RESOLVE reply, the cache-relative path it names, the lease
 * scope derived from that path, and the read-only open flags. Free of Samba
 * types; unit tested in tests/test-snapshot-access.cpp.
 */

#include "local_protocol.h"

#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

/*
 * The bridge response crosses an unauthenticated control-plane boundary, so
 * never treat it as an arbitrary path. Only non-empty relative paths without
 * empty/dot components, backslashes or control characters are accepted before
 * the fixed local cache root is joined.
 */
static inline bool kaimo_cache_relative_path_valid(const char *path)
{
	if (path == NULL || path[0] == '\0' || path[0] == '/' ||
	    strchr(path, '\\') != NULL)
		return false;
	for (const unsigned char *p = (const unsigned char *)path; *p != '\0'; p++) {
		if (*p < 0x20 || *p == 0x7f)
			return false;
	}

	const char *component = path;
	for (;;) {
		const char *slash = strchr(component, '/');
		size_t len = slash != NULL
			? (size_t)(slash - component) : strlen(component);
		if (len == 0 || (len == 1 && component[0] == '.') ||
		    (len == 2 && component[0] == '.' && component[1] == '.'))
			return false;
		if (slash == NULL)
			return true;
		component = slash + 1;
	}
}

/*
 * cache_relative is <share-id>/<@GMT>/<user-id>/...; the token's shared lease
 * lives at <share-id>/<@GMT>/.kaimo-lease, never inside a user projection.
 * Returns the length of the "<share-id>/<@GMT>" prefix, or -1 (EACCES) when
 * the path is invalid or has no component below the token directory.
 */
static inline ptrdiff_t kaimo_snapshot_lease_scope_length(
	const char *cache_relative)
{
	const char *first;
	const char *second;

	if (!kaimo_cache_relative_path_valid(cache_relative)) {
		errno = EACCES;
		return -1;
	}
	first = strchr(cache_relative, '/');
	second = first != NULL ? strchr(first + 1, '/') : NULL;
	if (first == NULL || second == NULL) {
		errno = EACCES;
		return -1;
	}
	return second - cache_relative;
}

/*
 * Historical opens are strictly O_RDONLY: reject any write/create/truncate/
 * append/tmpfile intent, and strip those bits again as defence in depth so a
 * lower VFS module can never receive them. Returns false with EROFS.
 */
static inline bool kaimo_snapshot_open_flags_readonly(int flags, int *output)
{
	const int unsafe_flags = O_CREAT | O_EXCL | O_TRUNC | O_APPEND;

	if (output == NULL || (flags & O_ACCMODE) != O_RDONLY ||
	    (flags & unsafe_flags) != 0
#ifdef O_TMPFILE
	    || (flags & O_TMPFILE) == O_TMPFILE
#endif
	    ) {
		errno = EROFS;
		return false;
	}
	*output = (flags & ~(O_ACCMODE | unsafe_flags)) | O_RDONLY;
	return true;
}

/*
 * Decode a SNAPSHOT_RESOLVE reply into NUL-terminated cache path and lease id.
 *   1  = resolved
 *   0  = no such version (NOT_FOUND with an empty payload)
 *  -1  = error / malformed; errno = ENAMETOOLONG when an output is too small
 * Wire format of OK: string cache_path, u64 version_size, string lease_id.
 */
static inline int kaimo_snapshot_resolve_decode(
	uint8_t status, const uint8_t *payload, uint32_t payload_length,
	char *cache_out, size_t cache_out_size,
	char *lease_out, size_t lease_out_size)
{
	struct kaimo_local_reader reader;
	const uint8_t *cache_path;
	uint32_t cache_path_length;
	const uint8_t *lease_id;
	uint32_t lease_id_length;
	uint64_t version_size;

	if (cache_out == NULL || cache_out_size == 0 ||
	    lease_out == NULL || lease_out_size == 0)
		return -1;
	if (status == KAIMO_LOCAL_STATUS_NOT_FOUND && payload_length == 0)
		return 0;
	if (status != KAIMO_LOCAL_STATUS_OK)
		return -1;

	kaimo_local_reader_init(&reader, payload, payload_length);
	if (!kaimo_local_reader_string(
		    &reader, &cache_path, &cache_path_length) ||
	    !kaimo_local_reader_u64(&reader, &version_size) ||
	    !kaimo_local_reader_string(&reader, &lease_id, &lease_id_length) ||
	    !kaimo_local_reader_finished(&reader) ||
	    cache_path_length == 0 || lease_id_length == 0)
		return -1;
	(void)version_size;

	if ((size_t)cache_path_length >= cache_out_size ||
	    (size_t)lease_id_length >= lease_out_size) {
		errno = ENAMETOOLONG;
		return -1;
	}
	memcpy(cache_out, cache_path, cache_path_length);
	cache_out[cache_path_length] = '\0';
	memcpy(lease_out, lease_id, lease_id_length);
	lease_out[lease_id_length] = '\0';
	return 1;
}

#endif
