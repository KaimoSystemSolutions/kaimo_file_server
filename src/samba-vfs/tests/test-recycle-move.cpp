#include "recycle_move.h"

#include <cassert>
#include <cerrno>
#include <filesystem>
#include <fstream>
#include <string>

namespace fs = std::filesystem;

static fs::path make_test_root()
{
	char pattern[] = "/tmp/kaimo-recycle-test-XXXXXX";
	char *created = mkdtemp(pattern);
	assert(created != nullptr);
	return fs::path(created);
}

static int open_directory(const fs::path& path)
{
	int descriptor = open(
		path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
	assert(descriptor >= 0);
	return descriptor;
}

static void test_path_boundary()
{
	assert(kaimo_recycle_path_is_inside(".RECYCLE_BIN"));
	assert(kaimo_recycle_path_is_inside(".recycle_bin/file.txt"));
	assert(!kaimo_recycle_path_is_inside(".RECYCLE_BIN_backup/file.txt"));
	assert(!kaimo_recycle_path_is_inside("folder/.RECYCLE_BIN/file.txt"));
}

static void test_move_and_collision()
{
	fs::path root = make_test_root();
	fs::create_directories(root / "docs");
	std::ofstream(root / "docs" / "report.txt") << "first";

	int root_fd = open_directory(root);
	int source_fd = open_directory(root / "docs");
	int destination_fd = kaimo_recycle_open_destination_parent(
		root_fd, "docs/report.txt");
	assert(destination_fd >= 0);

	char candidate[NAME_MAX + 1];
	assert(kaimo_recycle_candidate_leaf(
		candidate, sizeof(candidate), "report.txt",
		"2026-07-30_12-34-56", 0) == 0);
	assert(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == 0);
	assert(fs::exists(root / ".RECYCLE_BIN" / "docs" / "report.txt"));
	assert(!fs::exists(root / "docs" / "report.txt"));

	std::ofstream(root / "docs" / "report.txt") << "second";
	assert(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == -1);
	assert(errno == EEXIST);
	assert(kaimo_recycle_candidate_leaf(
		candidate, sizeof(candidate), "report.txt",
		"2026-07-30_12-34-56", 1) == 0);
	assert(kaimo_recycle_rename_noreplace(
		source_fd, "report.txt", destination_fd, candidate) == 0);
	assert(fs::exists(
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
	assert(kaimo_recycle_open_destination_parent(
		root_fd, "docs/report.txt") == -1);
	assert(errno == ELOOP || errno == ENOTDIR);

	close(root_fd);
	fs::remove_all(root);
	fs::remove_all(outside);
}

int main()
{
	test_path_boundary();
	test_move_and_collision();
	test_destination_symlink_is_rejected();
	return 0;
}
