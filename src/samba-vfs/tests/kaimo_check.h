/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
#ifndef KAIMO_CHECK_H
#define KAIMO_CHECK_H

/*
 * Test assertion for the standalone C++ component tests.
 *
 * Unlike <cassert>, CHECK is never compiled out by NDEBUG, so expressions with
 * side effects (CHECK(spool.initialize(error))) always run. A failure reports
 * file/line/expression and aborts, which also lets ASan/UBSan builds and the
 * Docker build step fail visibly on the first broken expectation.
 */

#include <cstdio>
#include <cstdlib>

#define CHECK(condition)                                                   \
	do {                                                               \
		if (!(condition)) {                                        \
			std::fprintf(stderr, "%s:%d: CHECK failed: %s\n",  \
				     __FILE__, __LINE__, #condition);      \
			std::fflush(stderr);                               \
			std::abort();                                      \
		}                                                          \
	} while (0)

#endif
