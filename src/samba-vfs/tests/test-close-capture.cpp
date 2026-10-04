#include "close_capture.h"

#include "kaimo_check.h"

#include <cerrno>
#include <cstdlib>
#include <dirent.h>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

namespace {

std::string make_directory(const std::string &parent, const char *name)
{
	std::string path = parent + "/" + name;
	CHECK(mkdir(path.c_str(), 0700) == 0);
	return path;
}

std::string read_file(const std::string &path)
{
	std::ifstream input(path, std::ios::binary);
	std::ostringstream content;
	content << input.rdbuf();
	return content.str();
}

void write_file(const std::string &path, const std::string &content)
{
	std::ofstream output(path, std::ios::binary | std::ios::trunc);
	output << content;
}

std::vector<std::string> entries(const std::string &directory)
{
	std::vector<std::string> names;
	DIR *handle = opendir(directory.c_str());
	CHECK(handle != nullptr);
	while (dirent *entry = readdir(handle)) {
		std::string name = entry->d_name;
		if (name != "." && name != "..")
			names.push_back(name);
	}
	closedir(handle);
	return names;
}

void test_random_id_and_stability(const std::string &root)
{
	char first[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1];
	char second[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1];
	CHECK(kaimo_random_capture_id(first));
	CHECK(kaimo_random_capture_id(second));
	CHECK(std::string(first).size() == KAIMO_CLOSE_CAPTURE_ID_BYTES);
	CHECK(std::string(first).find_first_not_of("0123456789abcdef") == std::string::npos);
	CHECK(std::string(first) != std::string(second));

	std::string file = root + "/stable";
	write_file(file, "x");
	struct stat before {};
	CHECK(stat(file.c_str(), &before) == 0);
	struct stat after = before;
	CHECK(kaimo_capture_source_stable(&before, &after));
	after.st_size++;
	CHECK(!kaimo_capture_source_stable(&before, &after));
	after = before;
	after.st_mtim.tv_nsec++;
	CHECK(!kaimo_capture_source_stable(&before, &after));
	after = before;
	after.st_ctim.tv_sec++;
	CHECK(!kaimo_capture_source_stable(&before, &after));
	after = before;
	after.st_ino++;
	CHECK(!kaimo_capture_source_stable(&before, &after));
}

void test_capture_publishes_exact_content(const std::string &root)
{
	std::string captures = make_directory(root, "captures");
	std::string source = root + "/source.bin";
	std::string content;
	for (int i = 0; i < 50000; ++i)
		content += std::to_string(i) + "\n";  // > 128 KiB: several chunks
	write_file(source, content);

	// Write-only handle: the capture must reopen the same inode for reading.
	int fd = open(source.c_str(), O_WRONLY);
	CHECK(fd >= 0);
	off_t position = lseek(fd, 17, SEEK_SET);
	char id[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1];
	char final_path[512];
	CHECK(kaimo_close_capture(fd, captures.c_str(), id, final_path, sizeof(final_path)));
	CHECK(lseek(fd, 0, SEEK_CUR) == position);  // Samba's offset untouched
	close(fd);

	CHECK(std::string(final_path) == captures + "/" + id + ".cap");
	CHECK(read_file(final_path) == content);
	struct stat st {};
	CHECK(stat(final_path, &st) == 0);
	CHECK((st.st_mode & 07777) == 0440);
	CHECK(entries(captures).size() == 1);

	// An empty file is a valid (empty) capture.
	std::string empty = root + "/empty";
	write_file(empty, "");
	fd = open(empty.c_str(), O_RDONLY);
	CHECK(kaimo_close_capture(fd, captures.c_str(), id, final_path, sizeof(final_path)));
	close(fd);
	CHECK(read_file(final_path).empty());
	CHECK(entries(captures).size() == 2);
}

void test_rejections_leave_nothing_behind(const std::string &root)
{
	std::string captures = make_directory(root, "rejections");
	std::string source = root + "/doc.txt";
	write_file(source, "content");
	char id[KAIMO_CLOSE_CAPTURE_ID_BYTES + 1];
	char final_path[512];

	errno = 0;
	CHECK(!kaimo_close_capture(-1, captures.c_str(), id, final_path, sizeof(final_path)));
	CHECK(errno == EBADF);

	int fd = open(source.c_str(), O_RDONLY);
	CHECK(fd >= 0);
	errno = 0;
	CHECK(!kaimo_close_capture(fd, nullptr, id, final_path, sizeof(final_path)));
	CHECK(errno == EINVAL);
	CHECK(!kaimo_close_capture(fd, captures.c_str(), id, nullptr, 1));
	CHECK(!kaimo_close_capture(fd, captures.c_str(), id, final_path, 0));

	// Missing capture directory.
	errno = 0;
	std::string missing = root + "/missing";
	CHECK(!kaimo_close_capture(fd, missing.c_str(), id, final_path, sizeof(final_path)));
	CHECK(errno == ENOENT);

	// A symlinked capture directory is never followed.
	std::string link = root + "/linked";
	CHECK(symlink(captures.c_str(), link.c_str()) == 0);
	errno = 0;
	CHECK(!kaimo_close_capture(fd, link.c_str(), id, final_path, sizeof(final_path)));
	CHECK(errno == ELOOP || errno == ENOTDIR);

	// Final path buffer too small.
	errno = 0;
	CHECK(!kaimo_close_capture(fd, captures.c_str(), id, final_path, 8));
	CHECK(errno == ENAMETOOLONG);

	// Capture directory not writable (only effective for non-root).
	if (geteuid() != 0) {
		CHECK(chmod(captures.c_str(), 0500) == 0);
		CHECK(!kaimo_close_capture(fd, captures.c_str(), id, final_path, sizeof(final_path)));
		CHECK(chmod(captures.c_str(), 0700) == 0);
	}
	close(fd);
	CHECK(entries(captures).empty());

	// Only regular files are captured.
	int directory_fd = open(captures.c_str(), O_RDONLY | O_DIRECTORY);
	errno = 0;
	CHECK(!kaimo_close_capture(directory_fd, captures.c_str(), id, final_path,
				   sizeof(final_path)));
	CHECK(errno == EINVAL);
	close(directory_fd);

	int pipe_fds[2];
	CHECK(pipe(pipe_fds) == 0);
	errno = 0;
	CHECK(!kaimo_close_capture(pipe_fds[0], captures.c_str(), id, final_path,
				   sizeof(final_path)));
	CHECK(errno == EINVAL);
	close(pipe_fds[0]);
	close(pipe_fds[1]);
	CHECK(entries(captures).empty());
}

void test_copy_errors(const std::string &root)
{
	std::string source = root + "/short.txt";
	write_file(source, "abc");
	int source_fd = open(source.c_str(), O_RDONLY);
	std::string target = root + "/target.txt";
	int target_fd = open(target.c_str(), O_WRONLY | O_CREAT | O_TRUNC, 0600);
	CHECK(source_fd >= 0 && target_fd >= 0);

	// Source shorter than the size recorded before the copy -> EIO.
	errno = 0;
	CHECK(!kaimo_capture_copy(source_fd, target_fd, 10));
	CHECK(errno == EIO);
	// Unreadable source descriptor.
	errno = 0;
	CHECK(!kaimo_capture_copy(target_fd, target_fd, 3));
	CHECK(errno == EBADF);
	// Unwritable target descriptor.
	errno = 0;
	CHECK(!kaimo_capture_copy(source_fd, source_fd, 3));
	CHECK(errno == EBADF);
	CHECK(kaimo_capture_copy(source_fd, target_fd, 3));
	CHECK(kaimo_capture_copy(source_fd, target_fd, 0));
	close(source_fd);
	close(target_fd);
	CHECK(read_file(target) == "abc");
}

}  // namespace

int main()
{
	char pattern[] = "/tmp/kaimo-close-capture-XXXXXX";
	char *root = mkdtemp(pattern);
	CHECK(root != nullptr);

	test_random_id_and_stability(root);
	test_capture_publishes_exact_content(root);
	test_rejections_leave_nothing_behind(root);
	test_copy_errors(root);

	std::string cleanup = std::string("rm -rf '") + root + "'";
	CHECK(std::system(cleanup.c_str()) == 0);
	std::cout << "close capture tests passed" << std::endl;
	return 0;
}
