/*
 * Kaimo File Server - Samba VFS Bridge
 *
 * Plugs into the SMB junction where the Kaimo Control Plane decides.
 * The data path remains native (always SMB_VFS_NEXT_*), Samba does I/O directly.
 *
 *   Phase 2a: Authorize TREE_CONNECT (connect hook -> CanAccessShareAsync).
 *   Phase 2b: File/path ACL (create_file hook -> FileService.OpenAsync parity)
 *             and directory listing filter (readdir hook -> ListAsync parity).
 *
 * Deliberately pure C without gRPC: gRPC complexity lives in the kaimo_authd
 * sidecar; the module only does simple Unix socket roundtrips (no fork/threads in smbd).
 */

#include "includes.h"
#include "smbd/smbd.h"

#include <stdlib.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <sys/un.h>

#undef DBGC_CLASS
#define DBGC_CLASS DBGC_VFS

#define KAIMO_AUTHD_SOCK_DEFAULT "/var/run/kaimo/authz.sock"

/* Per-connection stored in VFS handle (set at TREE_CONNECT). */
struct kaimo_conn_ctx {
	char user[128];
	char share[128];
};

static void kaimo_free_data(void **pptr)
{
	if (pptr != NULL && *pptr != NULL) {
		free(*pptr);
		*pptr = NULL;
	}
}

/* Fail behavior on infrastructure errors: default fail-open (allow),
 * with KAIMO_AUTHZ_FAILCLOSED=1 strictly deny. */
static bool kaimo_failmode_allow(void)
{
	const char *fc = getenv("KAIMO_AUTHZ_FAILCLOSED");
	return !(fc != NULL && fc[0] == '1');
}

/* Sends a request line to kaimo_authd and reads the response.
 * Return: 1 = ALLOW, 0 = DENY, -1 = Infrastructure error. */
static int kaimo_authz_send(const char *req, size_t len)
{
	const char *sock_path = getenv("KAIMO_AUTHD_SOCK");
	if (sock_path == NULL) sock_path = KAIMO_AUTHD_SOCK_DEFAULT;

	int fd = socket(AF_UNIX, SOCK_STREAM, 0);
	if (fd < 0) return -1;

	struct sockaddr_un addr;
	memset(&addr, 0, sizeof(addr));
	addr.sun_family = AF_UNIX;
	strlcpy(addr.sun_path, sock_path, sizeof(addr.sun_path));

	if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
		close(fd);
		return -1;
	}
	if (write(fd, req, len) != (ssize_t)len) {
		close(fd);
		return -1;
	}

	char buf[64];
	ssize_t r = read(fd, buf, sizeof(buf) - 1);
	close(fd);
	if (r <= 0) return -1;
	buf[r] = '\0';

	if (strncmp(buf, "ALLOW", 5) == 0) return 1;
	if (strncmp(buf, "DENY", 4) == 0)  return 0;
	return -1; /* "ERROR" or unexpected */
}

static bool kaimo_authz_connect(const char *service, const char *user)
{
	char req[512];
	int n = snprintf(req, sizeof(req), "CONNECT\t%s\t%s\n",
			 user ? user : "", service ? service : "");
	if (n <= 0 || n >= (int)sizeof(req)) return kaimo_failmode_allow();

	int d = kaimo_authz_send(req, (size_t)n);
	if (d < 0) {
		DBG_WARNING("kaimo_bridge: authd unreachable (connect), fail-%s\n",
			    kaimo_failmode_allow() ? "open" : "closed");
		return kaimo_failmode_allow();
	}
	return d == 1;
}

static bool kaimo_authz_open(const char *user, const char *share, const char *path,
			     bool want_read, bool want_write, bool wants_create)
{
	char flags[4];
	int fi = 0;
	if (want_read)    flags[fi++] = 'r';
	if (want_write)   flags[fi++] = 'w';
	if (wants_create) flags[fi++] = 'c';
	flags[fi] = '\0';

	char req[5120];
	int n = snprintf(req, sizeof(req), "OPEN\t%s\t%s\t%s\t%s\n",
			 user ? user : "", share ? share : "", flags, path ? path : "");
	if (n <= 0 || n >= (int)sizeof(req)) return kaimo_failmode_allow();

	int d = kaimo_authz_send(req, (size_t)n);
	if (d < 0) return kaimo_failmode_allow();
	return d == 1;
}

static bool kaimo_list_filter_enabled(void)
{
	const char *v = getenv("KAIMO_LIST_FILTER");
	return !(v != NULL && v[0] == '0'); /* default: on */
}

/* Fire-and-forget notification to kaimo_authd (no response expected),
 * so SMB Close/Delete/Rename doesn't wait for gRPC processing. */
static void kaimo_notify_send(const char *req, size_t len)
{
	const char *sock_path = getenv("KAIMO_AUTHD_SOCK");
	if (sock_path == NULL) sock_path = KAIMO_AUTHD_SOCK_DEFAULT;

	int fd = socket(AF_UNIX, SOCK_STREAM, 0);
	if (fd < 0) return;

	struct sockaddr_un addr;
	memset(&addr, 0, sizeof(addr));
	addr.sun_family = AF_UNIX;
	strlcpy(addr.sun_path, sock_path, sizeof(addr.sun_path));

	if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0)
		(void)write(fd, req, len);
	close(fd);
}

/* Builds the share-relative path from directory-fsp + at-relative name. */
static void kaimo_join_path(char *out, size_t n,
			    struct files_struct *dirfsp,
			    const struct smb_filename *name)
{
	const char *dir = (dirfsp != NULL && dirfsp->fsp_name != NULL)
				? dirfsp->fsp_name->base_name : NULL;
	const char *leaf = (name != NULL && name->base_name != NULL)
				? name->base_name : "";
	bool root = (dir == NULL) || dir[0] == '\0'
			|| (dir[0] == '.' && dir[1] == '\0');
	if (root)
		snprintf(out, n, "%s", leaf);
	else
		snprintf(out, n, "%s/%s", dir, leaf);
}

/* ---- TREE_CONNECT: authorize + store context (user/share) in handle ---- */
static int kaimo_connect(vfs_handle_struct *handle,
			 const char *service,
			 const char *user)
{
	bool is_ipc = (service != NULL && strequal(service, "IPC$"));

	if (!is_ipc && !kaimo_authz_connect(service, user)) {
		DBG_ERR("kaimo_bridge: CONNECT DENIED share=[%s] user=[%s]\n",
			service ? service : "(null)", user ? user : "(null)");
		errno = EACCES;
		return -1;
	}

	DBG_ERR("kaimo_bridge: CONNECT ALLOW share=[%s] user=[%s]\n",
		service ? service : "(null)", user ? user : "(null)");

	int ret = SMB_VFS_NEXT_CONNECT(handle, service, user);
	if (ret < 0) return ret;

	if (!is_ipc) {
		struct kaimo_conn_ctx *ctx = malloc(sizeof(*ctx));
		if (ctx != NULL) {
			strlcpy(ctx->user, user ? user : "", sizeof(ctx->user));
			strlcpy(ctx->share, service ? service : "", sizeof(ctx->share));
			SMB_VFS_HANDLE_SET_DATA(handle, ctx, kaimo_free_data,
						struct kaimo_conn_ctx, { free(ctx); });
		}
	}
	return ret;
}

static void kaimo_disconnect(vfs_handle_struct *handle)
{
	DBG_ERR("kaimo_bridge: DISCONNECT\n");
	SMB_VFS_NEXT_DISCONNECT(handle);
}

/* ---- File Open (CREATE): ACL decision via gRPC (FileService.OpenAsync) ---- */
static NTSTATUS kaimo_create_file(vfs_handle_struct *handle,
				  struct smb_request *req,
				  struct files_struct *dirfsp,
				  struct smb_filename *smb_fname,
				  uint32_t access_mask,
				  uint32_t share_access,
				  uint32_t create_disposition,
				  uint32_t create_options,
				  uint32_t file_attributes,
				  uint32_t oplock_request,
				  const struct smb2_lease *lease,
				  uint64_t allocation_size,
				  uint32_t private_flags,
				  struct security_descriptor *sd,
				  struct ea_list *ea_list,
				  files_struct **result,
				  int *pinfo,
				  const struct smb2_create_blobs *in_context_blobs,
				  struct smb2_create_blobs *out_context_blobs)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;

	if (ctx != NULL && smb_fname != NULL && smb_fname->base_name != NULL) {
		bool want_read  = (access_mask & SEC_FILE_READ_DATA) != 0;
		bool want_write = (access_mask & (SEC_FILE_WRITE_DATA | SEC_FILE_APPEND_DATA)) != 0;
		bool wants_create =
			(create_disposition == FILE_SUPERSEDE ||
			 create_disposition == FILE_CREATE ||
			 create_disposition == FILE_OPEN_IF ||
			 create_disposition == FILE_OVERWRITE_IF);

		if (!kaimo_authz_open(ctx->user, ctx->share, smb_fname->base_name,
				      want_read, want_write, wants_create)) {
			DBG_ERR("kaimo_bridge: CREATE DENIED path=[%s] user=[%s]\n",
				smb_fname->base_name, ctx->user);
			return NT_STATUS_ACCESS_DENIED;
		}
	}

	return SMB_VFS_NEXT_CREATE_FILE(handle, req, dirfsp, smb_fname, access_mask,
				       share_access, create_disposition, create_options,
				       file_attributes, oplock_request, lease,
				       allocation_size, private_flags, sd, ea_list,
				       result, pinfo, in_context_blobs, out_context_blobs);
}

/* ---- Directory listing filter: hide entries without read permission (ListAsync) ---- */
static struct dirent *kaimo_readdir(vfs_handle_struct *handle,
				    struct files_struct *dirfsp,
				    DIR *dirp)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	const char *dirpath = (dirfsp != NULL && dirfsp->fsp_name != NULL)
				? dirfsp->fsp_name->base_name : NULL;
	bool filter = kaimo_list_filter_enabled() && ctx != NULL && dirpath != NULL;
	bool at_root = (dirpath != NULL) &&
		       (dirpath[0] == '\0' || (dirpath[0] == '.' && dirpath[1] == '\0'));

	for (;;) {
		struct dirent *e = SMB_VFS_NEXT_READDIR(handle, dirfsp, dirp);
		if (e == NULL) return NULL;
		if (!filter) return e;

		const char *nm = e->d_name;
		/* Always allow "." and ".." */
		if (nm[0] == '.' && (nm[1] == '\0' || (nm[1] == '.' && nm[2] == '\0')))
			return e;

		char path[4096];
		if (at_root)
			snprintf(path, sizeof(path), "%s", nm);
		else
			snprintf(path, sizeof(path), "%s/%s", dirpath, nm);

		if (kaimo_authz_open(ctx->user, ctx->share, path, true, false, false))
			return e; /* readable -> show */

		DBG_INFO("kaimo_bridge: LIST hide [%s] user=[%s]\n", path, ctx->user);
		/* not readable -> skip, get next entry */
	}
}

/* ---- Close hook: written file -> versioning/index/ownership (Phase 3) ---- */
static int kaimo_close(vfs_handle_struct *handle, files_struct *fsp)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	bool modified = (fsp != NULL) && fsp->fsp_flags.modified;
	bool isdir = (fsp != NULL) && fsp->fsp_flags.is_directory;

	char path[4096];
	path[0] = '\0';
	if (fsp != NULL && fsp->fsp_name != NULL && fsp->fsp_name->base_name != NULL)
		strlcpy(path, fsp->fsp_name->base_name, sizeof(path));

	int ret = SMB_VFS_NEXT_CLOSE(handle, fsp);

	if (ret == 0 && ctx != NULL && modified && !isdir && path[0] != '\0') {
		char req[5120];
		int n = snprintf(req, sizeof(req), "CLOSE\t%s\t%s\t%s\n",
				 ctx->user, ctx->share, path);
		if (n > 0 && n < (int)sizeof(req))
			kaimo_notify_send(req, (size_t)n);
	}
	return ret;
}

/* ---- Delete hook: clean up search index (Phase 3) ---- */
static int kaimo_unlinkat(vfs_handle_struct *handle,
			  struct files_struct *srcdir_fsp,
			  const struct smb_filename *smb_fname,
			  int flags)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	char path[4096];
	kaimo_join_path(path, sizeof(path), srcdir_fsp, smb_fname);
	bool isdir = (flags & AT_REMOVEDIR) != 0;

	int ret = SMB_VFS_NEXT_UNLINKAT(handle, srcdir_fsp, smb_fname, flags);

	if (ret == 0 && ctx != NULL && path[0] != '\0') {
		char req[5120];
		int n = snprintf(req, sizeof(req), "DELETE\t%s\t%s\t%d\t%s\n",
				 ctx->user, ctx->share, isdir ? 1 : 0, path);
		if (n > 0 && n < (int)sizeof(req))
			kaimo_notify_send(req, (size_t)n);
	}
	return ret;
}

/* ---- Rename hook: ACL path + search index follow-up (Phase 3) ---- */
static int kaimo_renameat(vfs_handle_struct *handle,
			  struct files_struct *srcdir_fsp,
			  const struct smb_filename *smb_fname_src,
			  struct files_struct *dstdir_fsp,
			  const struct smb_filename *smb_fname_dst)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	char oldp[4096], newp[4096];
	kaimo_join_path(oldp, sizeof(oldp), srcdir_fsp, smb_fname_src);
	kaimo_join_path(newp, sizeof(newp), dstdir_fsp, smb_fname_dst);

	int ret = SMB_VFS_NEXT_RENAMEAT(handle, srcdir_fsp, smb_fname_src,
					dstdir_fsp, smb_fname_dst);

	if (ret == 0 && ctx != NULL && oldp[0] != '\0' && newp[0] != '\0') {
		/* is_directory not reliably known here -> 0 (file); directory
		 * renames are rare and treated as path updates. */
		char req[8192];
		int n = snprintf(req, sizeof(req), "RENAME\t%s\t%s\t0\t%s\t%s\n",
				 ctx->user, ctx->share, oldp, newp);
		if (n > 0 && n < (int)sizeof(req))
			kaimo_notify_send(req, (size_t)n);
	}
	return ret;
}

/* ---- Mkdir hook: index new directory + ownership (Phase 3) ---- */
static int kaimo_mkdirat(vfs_handle_struct *handle,
			 struct files_struct *dirfsp,
			 const struct smb_filename *smb_fname,
			 mode_t mode)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	char path[4096];
	kaimo_join_path(path, sizeof(path), dirfsp, smb_fname);

	int ret = SMB_VFS_NEXT_MKDIRAT(handle, dirfsp, smb_fname, mode);

	if (ret == 0 && ctx != NULL && path[0] != '\0') {
		char req[5120];
		int n = snprintf(req, sizeof(req), "MKDIR\t%s\t%s\t%s\n",
				 ctx->user, ctx->share, path);
		if (n > 0 && n < (int)sizeof(req))
			kaimo_notify_send(req, (size_t)n);
	}
	return ret;
}

static struct vfs_fn_pointers kaimo_bridge_fns = {
	.connect_fn     = kaimo_connect,
	.disconnect_fn  = kaimo_disconnect,
	.create_file_fn = kaimo_create_file,
	.readdir_fn     = kaimo_readdir,
	.close_fn       = kaimo_close,
	.unlinkat_fn    = kaimo_unlinkat,
	.renameat_fn    = kaimo_renameat,
	.mkdirat_fn     = kaimo_mkdirat,
};

static_decl_vfs;
NTSTATUS vfs_kaimo_bridge_init(TALLOC_CTX *ctx)
{
	return smb_register_vfs(SMB_VFS_INTERFACE_VERSION, "kaimo_bridge",
				&kaimo_bridge_fns);
}
