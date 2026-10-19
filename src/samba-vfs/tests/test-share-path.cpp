#include "kaimo_check.h"
#include <cstring>
#include <iostream>

#include "share_path.h"

static void expect_path(const char *connectpath, const char *path,
			const char *expected)
{
	CHECK(std::strcmp(
		       kaimo_share_path_canonical(connectpath, path),
		       expected) == 0);
}

int main()
{
	expect_path("/data/storage/share", nullptr, "");
	expect_path("/data/storage/share", "", "");
	expect_path("/data/storage/share", ".", "");
	expect_path("/data/storage/share", "./", "");
	expect_path("/data/storage/share", "./folder/file.txt",
		    "folder/file.txt");
	expect_path("/data/storage/share", "folder/file.txt",
		    "folder/file.txt");

	expect_path("/data/storage/share",
		    "/data/storage/share", "");
	expect_path("/data/storage/share",
		    "/data/storage/share/folder/file.txt",
		    "folder/file.txt");
	expect_path("/data/storage/share///",
		    "/data/storage/share/folder/file.txt",
		    "folder/file.txt");
	expect_path("/data/storage/share",
		    "/data/storage/share//./folder/file.txt",
		    "folder/file.txt");

	/* A byte prefix without a component boundary is unrelated. */
	expect_path("/data/storage/share",
		    "/data/storage/share-backup/file.txt",
		    "/data/storage/share-backup/file.txt");
	expect_path("/data/storage/share",
		    "/data/storage/shared/file.txt",
		    "/data/storage/shared/file.txt");

	/* Root shares need special treatment: '/' is both prefix and boundary. */
	expect_path("/", "/", "");
	expect_path("/", "/folder/file.txt", "folder/file.txt");
	expect_path("///", "//folder/file.txt", "folder/file.txt");

	expect_path(nullptr, "./folder/file.txt", "folder/file.txt");
	expect_path("", "folder/file.txt", "folder/file.txt");

	/* Every .kaimo-* name at the share root is reserved for the server. */
	CHECK(kaimo_share_path_reserved("/data/storage/share",
					".kaimo-snapshots/x"));
	CHECK(kaimo_share_path_reserved(
		"/data/storage/share",
		"/data/storage/share/.kaimo-close-captures/id"));
	CHECK(kaimo_share_path_reserved("/data/storage/share",
					"./.kaimo-lease"));
	CHECK(kaimo_share_path_reserved("/data/storage/share",
					"./././.kaimo-x"));
	CHECK(kaimo_share_path_reserved("/data/storage/share", "//.kaimo-x"));
	CHECK(kaimo_share_path_reserved(nullptr, ".kaimo-"));
	CHECK(!kaimo_share_path_reserved("/data/storage/share",
					 "dir/.kaimo-x"));
	CHECK(!kaimo_share_path_reserved("/data/storage/share", ".kaimo"));
	CHECK(!kaimo_share_path_reserved("/data/storage/share", "kaimo-x"));
	CHECK(!kaimo_share_path_reserved("/data/storage/share", ".Kaimo-x"));
	CHECK(!kaimo_share_path_reserved("/data/storage/share", ""));
	CHECK(!kaimo_share_path_reserved("/data/storage/share", nullptr));
	CHECK(!kaimo_share_path_reserved("/data/storage/share",
					 "/data/storage/share"));

	std::cout << "share path canonicalization tests passed\n";
	return 0;
}
