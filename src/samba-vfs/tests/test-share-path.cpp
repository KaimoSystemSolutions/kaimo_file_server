#include <cassert>
#include <cstring>
#include <iostream>

#include "share_path.h"

static void expect_path(const char *connectpath, const char *path,
			const char *expected)
{
	assert(std::strcmp(
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

	std::cout << "share path canonicalization tests passed\n";
	return 0;
}
