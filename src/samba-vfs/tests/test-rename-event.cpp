#include "rename_event.h"

#include <cassert>
#include <iostream>

int main()
{
	assert(kaimo_rename_event_directory_flag(S_IFDIR | 0750) == 1);
	assert(kaimo_rename_event_directory_flag(S_IFREG | 0640) == 0);
	assert(kaimo_rename_event_directory_flag(S_IFLNK | 0777) == 0);

	std::cout << "rename lifecycle type tests passed\n";
	return 0;
}
