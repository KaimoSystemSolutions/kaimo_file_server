/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SPDX-FileCopyrightText: 2026 Kaimo File Server */
/*
 * Coverage builds only (force-included by tests/run-suite.sh): long-running
 * daemons such as kaimo_authd stop on SIGTERM without running exit handlers,
 * so gcov would never write their counters. Dump them, then terminate with
 * the conventional signal status. Never compiled into production binaries.
 */
#pragma once

#include <csignal>
#include <cstdlib>

extern "C" void __gcov_dump(void);

namespace kaimo_coverage {

inline void flush_and_exit(int signal_number)
{
	__gcov_dump();
	std::_Exit(128 + signal_number);
}

struct SignalFlushInstaller {
	SignalFlushInstaller()
	{
		std::signal(SIGTERM, flush_and_exit);
		std::signal(SIGINT, flush_and_exit);
	}
};

inline SignalFlushInstaller install_signal_flush;

}  // namespace kaimo_coverage
