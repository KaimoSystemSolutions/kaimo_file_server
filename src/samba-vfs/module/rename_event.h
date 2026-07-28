#ifndef KAIMO_RENAME_EVENT_H
#define KAIMO_RENAME_EVENT_H

#include <stdint.h>
#include <sys/stat.h>

/*
 * Keep the lifecycle type derived from the source object that renameat moves.
 * The destination may be absent or may be displaced, so its type must never
 * determine which rename callback the bridge invokes.
 */
static inline uint8_t kaimo_rename_event_directory_flag(mode_t source_mode)
{
	return S_ISDIR(source_mode) ? 1U : 0U;
}

#endif
