#include "recycle_move.h"

#include "kaimo_check.h"
#include <cerrno>
#include <filesystem>
#include <fstream>
#include <string>

namespace fs = std::filesystem;

static fs::path make_test_root()
{
	char pattern[] = "/tmp/kaimo-recycle-test-XXXXXX";
	char *created = mkdtemp(pattern);
	CHECK(created != nullptr);
	return fs::path(created);
}

static int open_directory(const fs::path& path)
{
	int descriptor = open(
		path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
	CHECK(descriptor >= 0);
	return descriptor;
}

static void test_path_boundary()
{
	CHECK(kaimo_recycle_path_is_inside(".RECYCLE_BIN", 0));
	CHECK(kaimo_recycle_path_is_inside(".recycle_bin/file.txt", 0));
	CHECK(!kaimo_recycle_path_is_inside(".RECYCLE_BIN_backup/file.txt", 0));
	CHECK(!kaimo_recycle_path_is_inside("folder/.RECYCLE_BIN/file.txt", 0));

	/* Depth 1: the home-folder share, one bin per <userId>. */
	CHECK(kaimo_recycle_path_is_inside("uid/.RECYCLE_BIN", 1));
	CHECK(kaimo_recycle_path_is_inside("uid/.recycle_bin/a/b.txt", 1));
	CHECK(!kaimo_recycle_path_is_inside("uid", 1));
	CHECK(!kaimo_recycle_path_is_inside("uid/docs/.RECYCLE_BIN", 1));
	CHECK(kaimo_recycle_root_length("uid/docs/a.txt", 1) == 4);
	CHECK(kaimo_recycle_root_length("uid", 1) == -1);
	CHECK(kaimo_recycle_root_length("a.txt", 0) == 0);
}

static void test_move_and_collision()
{
	fs::path root = make_test_root();
	fs::create_directories(root / "docs");
	std::ofstream(root / "docs" / "report.txt") << "first";

	int root_fd = open_directory(root);
	int source_fd = open_directory(root / "docs");
	int destination_fd = kaimo_recycle_open_destination_parent(
		root_fd, "docs/report.txt", 0);
	CHECK(destination_fd >= 0);

	char candidate[NAME_MAX + 1];
	CHECK(kaimo_recycle_candidate_leaf(
		candidate, sizeof(candidate), "report.txt",
		"2026-07-30_12-34-56", 0) == 0);
	CHECK(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == 0);
	CHECK(fs::exists(root / ".RECYCLE_BIN" / "docs" / "report.txt"));
	CHECK(!fs::exists(root / "docs" / "report.txt"));

	std::ofstream(root / "docs" / "report.txt") << "second";
	CHECK(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == -1);
	CHECK(errno == EEXIST);
	CHECK(kaimo_recycle_candidate_leaf(
		candidate, sizeof(candidate), "report.txt",
		"2026-07-30_12-34-56", 1) == 0);
	CHECK(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == 0);
	CHECK(fs::exists(
		root / ".RECYCLE_BIN" / "docs" /
		"report.txt_2026-07-30_12-34-56"));

	close(destination_fd);
	close(source_fd);
	close(root_fd);
	fs::remove_all(root);
}

static void test_destination_symlink_is_rejected()
{
	fs::path root = make_test_root();
	fs::path outside = make_test_root();
	fs::create_directory(root / ".RECYCLE_BIN");
	fs::create_directory_symlink(outside, root / ".RECYCLE_BIN" / "docs");

	int root_fd = open_directory(root);
	CHECK(kaimo_recycle_open_destination_parent(
		root_fd, "docs/report.txt", 0) == -1);
	CHECK(errno == ELOOP || errno == ENOTDIR);

	close(root_fd);
	fs::remove_all(root);
	fs::remove_all(outside);
}

static void test_home_recycle_root()
{
	fs::path root = make_test_root();
	fs::create_directories(root / "uid" / "docs");
	std::ofstream(root / "uid" / "docs" / "report.txt") << "data";

	int root_fd = open_directory(root);
	int source_fd = open_directory(root / "uid" / "docs");
	int destination_fd = kaimo_recycle_open_destination_parent(
		root_fd, "uid/docs/report.txt", 1);
	CHECK(destination_fd >= 0);
	CHECK(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, "report.txt") == 0);
	CHECK(fs::exists(
		root / "uid" / ".RECYCLE_BIN" / "docs" / "report.txt"));
	CHECK(!fs::exists(root / ".RECYCLE_BIN"));
	close(destination_fd);
	close(source_fd);

	/* The recycle root itself has no bin, and is never created. */
	CHECK(kaimo_recycle_open_destination_parent(root_fd, "uid", 1) == -1);
	CHECK(errno == EINVAL);
	CHECK(kaimo_recycle_open_destination_parent(
		root_fd, "missing/a.txt", 1) == -1);
	CHECK(errno == ENOENT);
	CHECK(!fs::exists(root / "missing"));
	CHECK(kaimo_recycle_open_destination_parent(
		root_fd, "uid/a.txt", 2) == -1);
	CHECK(errno == EINVAL);

	close(root_fd);
	fs::remove_all(root);
}

/* The fallback for filesystems without RENAME_NOREPLACE (e.g. 9p/drvfs) must
 * never replace an existing bin entry, file or (empty) directory. */
static void test_checked_rename_fallback()
{
	fs::path root = make_test_root();
	fs::create_directories(root / "src");
	fs::create_directories(root / "bin" / "taken_dir");
	std::ofstream(root / "src" / "a.txt") << "new";
	std::ofstream(root / "bin" / "taken.txt") << "old";

	int source_fd = open_directory(root / "src");
	int bin_fd = open_directory(root / "bin");
	CHECK(kaimo_recycle_rename_checked(
		source_fd, "a.txt", bin_fd, "taken.txt") == -1);
	CHECK(errno == EEXIST);
	CHECK(kaimo_recycle_rename_checked(
		source_fd, "a.txt", bin_fd, "taken_dir") == -1);
	CHECK(errno == EEXIST);
	CHECK(kaimo_recycle_rename_checked(
		source_fd, "a.txt", bin_fd, "a.txt") == 0);
	CHECK(fs::exists(root / "bin" / "a.txt"));
	CHECK(!fs::exists(root / "src" / "a.txt"));

	close(bin_fd);
	close(source_fd);
	fs::remove_all(root);
}

int main()
{
	test_path_boundary();
	test_move_and_collision();
	test_destination_symlink_is_rejected();
	test_home_recycle_root();
	test_checked_rename_fallback();
	return 0;
}
