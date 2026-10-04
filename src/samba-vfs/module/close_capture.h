/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_CLOSE_CAPTURE_H
#define KAIMO_CLOSE_CAPTURE_H

/*
 * Durable capture of the exact content behind a closing SMB handle (POSIX
 * part of the close hook in vfs_kaimo_bridge.c, unit tested standalone in
 * tests/test-close-capture.cpp).
 *
 * The capture is taken from the already-open descriptor, never a reopened
 * pathname. FICLONE gives a constant-time CoW snapshot where available; the
 * fallback copies with pread so it cannot disturb Samba's file position and
 * rejects a source whose size/timestamps changed during the copy. The result
 * is published under a random name with an atomic no-replace linkat.
 */

#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#ifdef __linux__
#include <linux/fs.h>
#endif

#define KAIMO_CLOSE_CAPTURE_ID_BYTES 32
#define KAIMO_CLOSE_CAPTURE_COPY_BUFFER (128 * 1024)

static inline bool kaimo_random_capture_id(
	char id[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1])
{
	uint8_t random_bytes[KAIMO_CLOSE_CAPTURE_ID_BYTES / 2];
	size_t offset = 0;
	static const char hex[] = "0123456789abcdef";

	while (offset < sizeof(random_bytes)) {
		ssize_t result = getrandom(
			random_bytes + offset, sizeof(random_bytes) - offset, 0);
		if (result < 0 && errno == EINTR)
			continue;
		if (result <= 0)
			return false;
		offset += (size_t)result;
	}
	for (size_t i = 0; i < sizeof(random_bytes); i++) {
		id[i * 2] = hex[random_bytes[i] >> 4];
		id[i * 2 + 1] = hex[random_bytes[i] & 0x0f];
	}
	id[KAIMO_CLOSE_CAPTURE_ID_BYTES] = '\0';
	return true;
}

static inline bool kaimo_capture_source_stable(const struct stat *before,
					       const struct stat *after)
{
	return before->st_dev == after->st_dev &&
	       before->st_ino == after->st_ino &&
	       before->st_size == after->st_size &&
	       before->st_mtim.tv_sec == after->st_mtim.tv_sec &&
	       before->st_mtim.tv_nsec == after->st_mtim.tv_nsec &&
	       before->st_ctim.tv_sec == after->st_ctim.tv_sec &&
	       before->st_ctim.tv_nsec == after->st_ctim.tv_nsec;
}

/* Copy size bytes from source_fd to capture_fd with positional I/O. */
static inline bool kaimo_capture_copy(int source_fd, int capture_fd,
				      off_t size)
{
	uint8_t *buffer = (uint8_t *)malloc(KAIMO_CLOSE_CAPTURE_COPY_BUFFER);
	off_t cursor = 0;

	if (buffer == NULL) {
		errno = ENOMEM;
		return false;
	}
	while (cursor < size) {
		size_t requested = (size_t)(size - cursor) <
				   (size_t)KAIMO_CLOSE_CAPTURE_COPY_BUFFER
			? (size_t)(size - cursor)
			: (size_t)KAIMO_CLOSE_CAPTURE_COPY_BUFFER;
		ssize_t count = pread(source_fd, buffer, requested, cursor);
		if (count < 0 && errno == EINTR)
			continue;
		if (count <= 0) {
			int saved_errno = count == 0 ? EIO : errno;
			free(buffer);
			errno = saved_errno;
			return false;
		}
		size_t written = 0;
		while (written < (size_t)count) {
			ssize_t result = pwrite(
				capture_fd, buffer + written,
				(size_t)count - written,
				cursor + (off_t)written);
			if (result < 0 && errno == EINTR)
				continue;
			if (result <= 0) {
				int saved_errno = result == 0 ? EIO : errno;
				free(buffer);
				errno = saved_errno;
				return false;
			}
			written += (size_t)result;
		}
		cursor += count;
	}
	free(buffer);
	return true;
}

/*
 * Capture the regular file open at samba_fd into capture_dir_path (which must
 * be a real directory, not a symlink) as <id>.cap with mode 0440.
 * On success capture_id holds the 32-hex id and final_path the absolute path.
 * On failure no temporary or final file remains and errno is set.
 */
static inline bool kaimo_close_capture(
	int samba_fd, const char *capture_dir_path,
	char capture_id[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1],
	char *final_path, size_t final_path_size)
{
	char procfd_path[64];
	char temporary_name[96];
	char final_name[64];
	struct stat before;
	struct stat after;
	struct stat reopened;
	int source_fd = -1;
	int directory_fd = -1;
	int capture_fd = -1;
	bool copied = false;
	bool names_ready = false;
	int saved_errno;
	int length;

	if (samba_fd < 0) {
		errno = EBADF;
		return false;
	}
	if (capture_dir_path == NULL || capture_id == NULL ||
	    final_path == NULL || final_path_size == 0) {
		errno = EINVAL;
		return false;
	}
	if (fstat(samba_fd, &before) != 0)
		return false;
	if (!S_ISREG(before.st_mode)) {
		errno = EINVAL;
		return false;
	}
	/* A client may have requested a write-only handle. Reopening this
	 * procfd obtains a readable description of the same already-resolved
	 * inode; it does not resolve the mutable share pathname. */
	length = snprintf(procfd_path, sizeof(procfd_path),
			  "/proc/self/fd/%d", samba_fd);
	if (length <= 0 || (size_t)length >= sizeof(procfd_path)) {
		errno = EOVERFLOW;
		return false;
	}
	source_fd = open(procfd_path, O_RDONLY | O_CLOEXEC);
	if (source_fd < 0)
		return false;
	if (fstat(source_fd, &reopened) != 0)
		goto done;
	if (reopened.st_dev != before.st_dev ||
	    reopened.st_ino != before.st_ino) {
		errno = ESTALE;
		goto done;
	}
	if (!kaimo_random_capture_id(capture_id))
		goto done;

	length = snprintf(temporary_name, sizeof(temporary_name),
			  ".%s.%lld.tmp", capture_id, (long long)getpid());
	if (length <= 0 || (size_t)length >= sizeof(temporary_name)) {
		errno = EOVERFLOW;
		goto done;
	}
	snprintf(final_name, sizeof(final_name), "%s.cap", capture_id);
	length = snprintf(final_path, final_path_size, "%s/%s",
			  capture_dir_path, final_name);
	if (length <= 0 || (size_t)length >= final_path_size) {
		errno = ENAMETOOLONG;
		goto done;
	}

	directory_fd = open(capture_dir_path,
			    O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
	if (directory_fd < 0)
		goto done;
	names_ready = true;
	capture_fd = openat(
		directory_fd, temporary_name,
		O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0440);
	if (capture_fd < 0)
		goto done;

#ifdef FICLONE
	if (ioctl(capture_fd, FICLONE, source_fd) == 0) {
		copied = true;
	} else if (ftruncate(capture_fd, 0) != 0) {
		goto done;
	}
#endif
	if (!copied && !kaimo_capture_copy(source_fd, capture_fd, before.st_size))
		goto done;

	if (fstat(source_fd, &after) != 0 ||
	    !kaimo_capture_source_stable(&before, &after)) {
		errno = EBUSY;
		goto done;
	}
	if (fchmod(capture_fd, 0440) != 0 || fsync(capture_fd) != 0)
		goto done;
	if (close(capture_fd) != 0) {
		capture_fd = -1;
		goto done;
	}
	capture_fd = -1;

	/* linkat is an atomic no-replace publication; the random final name
	 * cannot overwrite an existing capture even under a compromised peer. */
	if (linkat(directory_fd, temporary_name, directory_fd, final_name, 0) != 0)
		goto done;
	if (unlinkat(directory_fd, temporary_name, 0) != 0)
		goto done;
	if (fsync(directory_fd) != 0)
		goto done;

	close(directory_fd);
	close(source_fd);
	return true;

done:
	saved_errno = errno;
	if (capture_fd >= 0)
		close(capture_fd);
	if (source_fd >= 0)
		close(source_fd);
	if (directory_fd >= 0) {
		if (names_ready) {
			unlinkat(directory_fd, temporary_name, 0);
			unlinkat(directory_fd, final_name, 0);
		}
		close(directory_fd);
	}
	errno = saved_errno;
	return false;
}

#endif
