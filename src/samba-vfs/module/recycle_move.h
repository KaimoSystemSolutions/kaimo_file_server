#ifndef KAIMO_RECYCLE_MOVE_H
#define KAIMO_RECYCLE_MOVE_H

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#ifdef __linux__
#include <linux/fs.h>
#endif

#define KAIMO_RECYCLE_DIRECTORY ".RECYCLE_BIN"
#define KAIMO_RECYCLE_DIRECTORY_MODE 02770

static inline unsigned char kaimo_recycle_ascii_lower(unsigned char value)
{
	return value >= 'A' && value <= 'Z'
		? (unsigned char)(value - 'A' + 'a')
		: value;
}

static inline bool kaimo_recycle_component_equal_ci(
	const char *value, size_t length, const char *expected)
{
	size_t expected_length = strlen(expected);
	if (length != expected_length)
		return false;
	for (size_t offset = 0; offset < length; ++offset) {
		if (kaimo_recycle_ascii_lower((unsigned char)value[offset]) !=
		    kaimo_recycle_ascii_lower((unsigned char)expected[offset]))
			return false;
	}
	return true;
}

static inline bool kaimo_recycle_path_is_inside(const char *path)
{
	if (path == NULL)
		return false;
	const char *separator = strchr(path, '/');
	size_t first_length = separator != NULL
		? (size_t)(separator - path)
		: strlen(path);
	return kaimo_recycle_component_equal_ci(
		path, first_length, KAIMO_RECYCLE_DIRECTORY);
}

static inline bool kaimo_recycle_valid_component(
	const char *component, size_t length)
{
	return component != NULL &&
	       length != 0 &&
	       !(length == 1 && component[0] == '.') &&
	       !(length == 2 && component[0] == '.' && component[1] == '.') &&
	       memchr(component, '/', length) == NULL &&
	       memchr(component, '\0', length) == NULL;
}

static inline int kaimo_recycle_open_child_directory(
	int parent_fd, const char *name)
{
	int child_fd = openat(
		parent_fd, name,
		O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
	if (child_fd >= 0)
		return child_fd;
	if (errno != ENOENT)
		return -1;
	if (mkdirat(parent_fd, name, KAIMO_RECYCLE_DIRECTORY_MODE) != 0 &&
	    errno != EEXIST)
		return -1;
	return openat(
		parent_fd, name,
		O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
}

/*
 * Opens (and, where absent, creates) .RECYCLE_BIN plus the source path's
 * parent hierarchy below it. Every component is traversed with O_NOFOLLOW,
 * so a client-created symlink cannot redirect a recycle move outside the
 * share. The returned descriptor belongs to the caller.
 */
static inline int kaimo_recycle_open_destination_parent(
	int share_root_fd, const char *logical_source)
{
	if (share_root_fd < 0 || logical_source == NULL ||
	    logical_source[0] == '\0') {
		errno = EINVAL;
		return -1;
	}

	int current_fd = kaimo_recycle_open_child_directory(
		share_root_fd, KAIMO_RECYCLE_DIRECTORY);
	if (current_fd < 0)
		return -1;

	const char *cursor = logical_source;
	for (;;) {
		const char *separator = strchr(cursor, '/');
		if (separator == NULL)
			break;
		size_t length = (size_t)(separator - cursor);
		if (!kaimo_recycle_valid_component(cursor, length) ||
		    length > NAME_MAX) {
			close(current_fd);
			errno = length > NAME_MAX ? ENAMETOOLONG : EINVAL;
			return -1;
		}

		char component[NAME_MAX + 1];
		memcpy(component, cursor, length);
		component[length] = '\0';
		int next_fd = kaimo_recycle_open_child_directory(
			current_fd, component);
		int saved_errno = errno;
		close(current_fd);
		if (next_fd < 0) {
			errno = saved_errno;
			return -1;
		}
		current_fd = next_fd;
		cursor = separator + 1;
	}

	size_t leaf_length = strlen(cursor);
	if (!kaimo_recycle_valid_component(cursor, leaf_length) ||
	    leaf_length > NAME_MAX) {
		close(current_fd);
		errno = leaf_length > NAME_MAX ? ENAMETOOLONG : EINVAL;
		return -1;
	}
	return current_fd;
}

static inline const char *kaimo_recycle_basename(const char *logical_source)
{
	const char *separator = strrchr(logical_source, '/');
	return separator != NULL ? separator + 1 : logical_source;
}

static inline int kaimo_recycle_timestamp(char output[20])
{
	time_t now = time(NULL);
	struct tm utc;
	if (now == (time_t)-1 || gmtime_r(&now, &utc) == NULL)
		return -1;
	return strftime(output, 20, "%Y-%m-%d_%H-%M-%S", &utc) == 19
		? 0
		: -1;
}

/*
 * attempt 0 keeps the requested leaf. Later attempts match FileSystemStorage's
 * "_yyyy-MM-dd_HH-mm-ss" suffix; a numeric tail handles multiple collisions
 * within the same second without overwriting an existing recycle entry.
 */
static inline int kaimo_recycle_candidate_leaf(
	char *output, size_t output_size, const char *source_leaf,
	const char timestamp[20], unsigned int attempt)
{
	int length;
	if (attempt == 0) {
		length = snprintf(output, output_size, "%s", source_leaf);
	} else if (attempt == 1) {
		length = snprintf(
			output, output_size, "%s_%s", source_leaf, timestamp);
	} else {
		length = snprintf(
			output, output_size, "%s_%s_%u",
			source_leaf, timestamp, attempt);
	}
	if (length < 0 || (size_t)length >= output_size ||
	    length > NAME_MAX) {
		errno = ENAMETOOLONG;
		return -1;
	}
	return 0;
}

static inline int kaimo_recycle_rename_noreplace(
	int source_parent_fd, const char *source_leaf,
	int destination_parent_fd, const char *destination_leaf)
{
#if defined(__linux__) && defined(SYS_renameat2) && defined(RENAME_NOREPLACE)
	return (int)syscall(
		SYS_renameat2,
		source_parent_fd, source_leaf,
		destination_parent_fd, destination_leaf,
		RENAME_NOREPLACE);
#else
	(void)source_parent_fd;
	(void)source_leaf;
	(void)destination_parent_fd;
	(void)destination_leaf;
	errno = ENOTSUP;
	return -1;
#endif
}

#endif
