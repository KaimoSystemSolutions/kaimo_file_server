/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_VFS_ENV_H
#define KAIMO_VFS_ENV_H

/*
 * Parsing of the VFS module's environment configuration. The callers pass the
 * raw getenv() value so the rules are unit tested without touching the
 * process environment (tests/test-vfs-env.cpp).
 */

#include <errno.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

/*
 * Millisecond deadline from a decimal value in [minimum, maximum]. Unset or
 * empty selects the default silently; anything else that is not a plain
 * in-range decimal also selects the default and sets *invalid so the caller
 * can log it. A deadline can therefore never be disabled by configuration.
 */
static inline uint32_t kaimo_vfs_parse_timeout_ms(
	const char *configured, uint32_t default_value, uint32_t minimum,
	uint32_t maximum, bool *invalid)
{
	char *end = NULL;
	unsigned long parsed;

	if (invalid != NULL)
		*invalid = false;
	if (configured == NULL || configured[0] == '\0')
		return default_value;
	errno = 0;
	parsed = strtoul(configured, &end, 10);
	if (errno != 0 || end == configured || *end != '\0' ||
	    parsed < minimum || parsed > maximum) {
		if (invalid != NULL)
			*invalid = true;
		return default_value;
	}
	return (uint32_t)parsed;
}

/* Kill switches default to enabled; only a leading '0' disables them. */
static inline bool kaimo_vfs_switch_enabled(const char *value)
{
	return !(value != NULL && value[0] == '0');
}

#endif
