#ifndef KAIMO_SHARE_PATH_H
#define KAIMO_SHARE_PATH_H

#include <stdbool.h>
#include <stddef.h>
#include <string.h>

/*
 * Return the canonical share-relative view of a Samba path without allocating.
 *
 * Samba can supply either an already-relative path or an absolute path prefixed
 * by the connection's connectpath.  Only strip a complete path component:
 * connectpath "/share" must never match "/share-backup".  A root connectpath
 * is handled explicitly because its separator is the prefix itself.
 *
 * The returned pointer is borrowed from path.  NULL and the share-root spellings
 * ".", "./", and an exact connectpath all canonicalize to the empty string.
 */
static inline const char *kaimo_share_path_canonical(
	const char *connectpath, const char *path)
{
	size_t connectpath_length;
	bool connectpath_matches = false;

	if (path == NULL)
		return "";

	connectpath_length =
		connectpath != NULL ? strlen(connectpath) : 0;
	while (connectpath_length > 1 &&
	       connectpath[connectpath_length - 1] == '/')
		connectpath_length--;

	if (connectpath_length == 1 && connectpath[0] == '/') {
		connectpath_matches = path[0] == '/';
	} else if (connectpath_length > 0 &&
		   strncmp(path, connectpath, connectpath_length) == 0 &&
		   (path[connectpath_length] == '\0' ||
		    path[connectpath_length] == '/')) {
		connectpath_matches = true;
	}

	if (connectpath_matches) {
		path += connectpath_length;
		while (path[0] == '/')
			path++;
	}

	while (path[0] == '.' && path[1] == '/')
		path += 2;
	if (path[0] == '.' && path[1] == '\0')
		path++;
	return path;
}

#endif
