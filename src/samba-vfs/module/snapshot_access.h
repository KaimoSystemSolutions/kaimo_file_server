/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_SNAPSHOT_ACCESS_H
#define KAIMO_SNAPSHOT_ACCESS_H

/*
 * Pure validation for the @GMT snapshot data path of vfs_kaimo_bridge.c:
 * the @GMT label, the read-only create/access contract, the SNAPSHOT_RESOLVE
 * reply, the cache-relative path it names, the lease scope derived from that
 * path, and the read-only open flags. Free of Samba types; unit tested in
 * tests/test-snapshot-access.cpp.
 */

#include "local_protocol.h"

#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <time.h>

/* Samba access-mask and create values used below. vfs_kaimo_bridge.c fails
 * the build with _Static_assert if the pinned Samba ever disagrees. */
#define KAIMO_FILE_GENERIC_READ 0x00120089U
#define KAIMO_FILE_GENERIC_EXECUTE 0x001200a0U
#define KAIMO_GENERIC_READ_ACCESS 0x80000000U
#define KAIMO_GENERIC_EXECUTE_ACCESS 0x20000000U
#define KAIMO_MAXIMUM_ALLOWED_ACCESS 0x02000000U
#define KAIMO_FILE_OPEN 1U
#define KAIMO_FILE_DELETE_ON_CLOSE 0x00001000U

/* Snapshot handles may read data/EA/attributes, traverse/execute, read their
 * security descriptor, and synchronize. Every right that can mutate the
 * object or its namespace is excluded. MAXIMUM_ALLOWED is accepted as a
 * request form, but the control-plane result is clamped to this mask. */
#define KAIMO_SNAPSHOT_ALLOWED_ACCESS \
	(KAIMO_FILE_GENERIC_READ | KAIMO_FILE_GENERIC_EXECUTE)
/* FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES |
 * FILE_DELETE_CHILD | DELETE_ACCESS | WRITE_DAC_ACCESS | WRITE_OWNER_ACCESS |
 * GENERIC_WRITE_ACCESS | GENERIC_ALL_ACCESS */
#define KAIMO_SNAPSHOT_MUTATING_ACCESS 0x500d0156U

/* Converts a Unix time to the Windows label "@GMT-YYYY.MM.DD-HH.MM.SS"
 * (24 chars). out must hold >= 25 bytes. */
static inline bool kaimo_snapshot_gmt_label(time_t t, char *out, size_t out_sz)
{
	struct tm tmv;
	if (gmtime_r(&t, &tmv) == NULL) return false;
	return strftime(out, out_sz, "@GMT-%Y.%m.%d-%H.%M.%S", &tmv) == 24;
}

/*
 * Samba may populate allocation/security/EA arguments while opening an
 * existing object even when the SMB request uses FILE_OPEN. They are create
 * metadata, not mutation intent in that disposition, so only the access mask,
 * disposition and options decide. The actual data-path open is independently
 * forced to O_RDONLY without create/truncate/append, cache objects are POSIX
 * read-only, and every namespace mutation hook rejects a timewarp path.
 * Returns false with EROFS on any write intent.
 */
static inline bool kaimo_snapshot_create_readonly(
	uint32_t access_mask, uint32_t create_disposition,
	uint32_t create_options)
{
	if ((access_mask & KAIMO_SNAPSHOT_MUTATING_ACCESS) != 0 ||
	    create_disposition != KAIMO_FILE_OPEN ||
	    (create_options & KAIMO_FILE_DELETE_ON_CLOSE) != 0) {
		errno = EROFS;
		return false;
	}
	return true;
}

static inline uint32_t kaimo_snapshot_granted_access(
	uint32_t requested, uint32_t granted)
{
	uint32_t expanded = requested;

	if ((expanded & KAIMO_GENERIC_READ_ACCESS) != 0) {
		expanded &= ~KAIMO_GENERIC_READ_ACCESS;
		expanded |= KAIMO_FILE_GENERIC_READ;
	}
	if ((expanded & KAIMO_GENERIC_EXECUTE_ACCESS) != 0) {
		expanded &= ~KAIMO_GENERIC_EXECUTE_ACCESS;
		expanded |= KAIMO_FILE_GENERIC_EXECUTE;
	}
	if ((expanded & KAIMO_MAXIMUM_ALLOWED_ACCESS) != 0)
		expanded |= KAIMO_SNAPSHOT_ALLOWED_ACCESS;

	/* Never add a right merely because the control plane returned a broader
	 * mask than requested, and never retain a mutating snapshot right. */
	return granted & expanded & KAIMO_SNAPSHOT_ALLOWED_ACCESS;
}

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
