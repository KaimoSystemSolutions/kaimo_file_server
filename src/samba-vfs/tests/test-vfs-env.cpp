#include "vfs_env.h"

#include "kaimo_check.h"

#include <iostream>

int main()
{
	bool invalid = true;

	CHECK(kaimo_vfs_parse_timeout_ms(nullptr, 250, 10, 60000, &invalid) == 250);
	CHECK(!invalid);
	CHECK(kaimo_vfs_parse_timeout_ms("", 250, 10, 60000, &invalid) == 250);
	CHECK(!invalid);
	CHECK(kaimo_vfs_parse_timeout_ms("300", 250, 10, 60000, &invalid) == 300);
	CHECK(!invalid);
	CHECK(kaimo_vfs_parse_timeout_ms("10", 250, 10, 60000, &invalid) == 10);
	CHECK(kaimo_vfs_parse_timeout_ms("60000", 250, 10, 60000, &invalid) == 60000);
	CHECK(!invalid);

	for (const char *bad : {"9", "60001", "-1", "abc", "300ms", "3 00",
				"99999999999999999999999", "0x10"}) {
		invalid = false;
		CHECK(kaimo_vfs_parse_timeout_ms(bad, 250, 10, 60000, &invalid) == 250);
		CHECK(invalid);
	}
	CHECK(kaimo_vfs_parse_timeout_ms("5", 77, 10, 100, nullptr) == 77);
	CHECK(kaimo_vfs_parse_timeout_ms("50", 77, 10, 100, nullptr) == 50);

	CHECK(kaimo_vfs_switch_enabled(nullptr));
	CHECK(kaimo_vfs_switch_enabled(""));
	CHECK(kaimo_vfs_switch_enabled("1"));
	CHECK(kaimo_vfs_switch_enabled("off"));
	CHECK(!kaimo_vfs_switch_enabled("0"));
	CHECK(!kaimo_vfs_switch_enabled("0ff"));

	std::cout << "vfs env tests passed" << std::endl;
	return 0;
}
