/*
 * Kaimo File Server - Samba VFS Bridge
 *
 * Plugs into the SMB junction where the Kaimo Control Plane decides.
 * The data path remains native (always SMB_VFS_NEXT_*), Samba does I/O directly.
 *
 *   Phase 2a: Authorize TREE_CONNECT (connect hook -> CanAccessShareAsync).
 *   Phase 2b: File/path ACL (create_file hook -> FileService.OpenAsync parity)
 *             and directory listing filter (readdir hook -> ListAsync parity).
 *   Phase 3:  Close/Delete/Rename/Mkdir event hooks (versioning/index/ownership).
 *   Phase 5:  @GMT snapshots ("Previous Versions"): get_shadow_copy_data enumerates
 *             version tokens; a timewarp (smb_fname->twrp) on open/stat is resolved
 *             to a materialized, decompressed version copy in an isolated global
 *             cache outside every share. Backed by IFileVersionService via the bridge.
 *
 * Deliberately pure C without gRPC: gRPC complexity lives in the kaimo_authd
 * sidecar; the module only does simple Unix socket roundtrips (no fork/threads in smbd).
 */

#include "includes.h"
#include "smbd/smbd.h"
#include "include/ntioctl.h" /* struct shadow_copy_data / SHADOW_COPY_LABEL (@GMT) */

#include <stdlib.h>
#include <stdint.h>
#include <limits.h>
#include <fcntl.h>
#include <time.h>
#include <sys/socket.h>
#include <sys/un.h>

#include "local_protocol.h"

#undef DBGC_CLASS
#define DBGC_CLASS DBGC_VFS

#define KAIMO_AUTHD_SOCK_DEFAULT "/var/run/kaimo/authz.sock"
/* Samba 4.19.5 FILE_GENERIC_ALL after generic expansion. OPEN replies may
 * contain only these specific file/directory and standard access bits. */
#define KAIMO_SAMBA_SPECIFIC_ACCESS 0x001f01ffU

/* Per-connection stored in VFS handle (set at TREE_CONNECT). */
struct kaimo_conn_ctx {
	char user[128];
	char share[128];
};

struct kaimo_local_request {
	uint8_t payload[KAIMO_LOCAL_MAX_REQUEST_PAYLOAD];
	struct kaimo_local_builder builder;
};

static void kaimo_free_data(void **pptr)
{
	if (pptr != NULL && *pptr != NULL) {
		free(*pptr);
		*pptr = NULL;
	}
}

/* Fail behavior on infrastructure errors (authd/bridge unreachable):
 * default fail-CLOSED (deny) — a bridge outage must not silently grant access.
 * Set KAIMO_AUTHZ_FAILOPEN=1 to restore the old permissive behavior (allow on
 * error), e.g. for availability-over-security dev setups. */
static bool kaimo_failmode_allow(void)
{
	const char *fo = getenv("KAIMO_AUTHZ_FAILOPEN");
	return fo != NULL && fo[0] == '1';
}

static void kaimo_request_init(struct kaimo_local_request *request)
{
	kaimo_local_builder_init(&request->builder, request->payload,
				 sizeof(request->payload));
}

static bool kaimo_request_ready(const char *operation,
				const struct kaimo_local_request *request)
{
	if (!request->builder.valid) {
		DBG_WARNING("kaimo_bridge: %s binary request exceeds %u bytes, denied\n",
			    operation, KAIMO_LOCAL_MAX_REQUEST_PAYLOAD);
		errno = ENAMETOOLONG;
		return false;
	}
	return true;
}

static int kaimo_authd_connect(void)
{
	const char *sock_path = getenv("KAIMO_AUTHD_SOCK");
	if (sock_path == NULL)
		sock_path = KAIMO_AUTHD_SOCK_DEFAULT;

	int fd = socket(AF_UNIX, SOCK_STREAM, 0);
	if (fd < 0)
		return -1;

	struct sockaddr_un addr;
	memset(&addr, 0, sizeof(addr));
	addr.sun_family = AF_UNIX;
	strlcpy(addr.sun_path, sock_path, sizeof(addr.sun_path));
	if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
		close(fd);
		return -1;
	}
	return fd;
}

/* Write one complete request frame and read one complete response frame.
 * Header lengths are validated before response bytes are read into caller
 * storage. Return 0 on success, -1 on transport failure, -2 on malformed wire
 * data. */
static int kaimo_roundtrip(uint8_t operation,
			   const struct kaimo_local_request *request,
			   struct kaimo_local_frame_header *response,
			   uint8_t *payload, size_t payload_capacity)
{
	int fd = kaimo_authd_connect();
	if (fd < 0)
		return -1;

	if (kaimo_local_send_frame(
		    fd, operation, KAIMO_LOCAL_KIND_REQUEST,
		    KAIMO_LOCAL_STATUS_NONE, request->payload,
		    request->builder.length) != 0) {
		close(fd);
		return -1;
	}

	if (kaimo_local_read_frame_header(fd, response) != 0) {
		int saved_errno = errno;
		close(fd);
		return (saved_errno == EPROTO || saved_errno == EMSGSIZE) ? -2 : -1;
	}
	if (response->kind != KAIMO_LOCAL_KIND_RESPONSE ||
	    (response->operation != operation &&
	     !(response->operation == KAIMO_LOCAL_OP_NONE &&
	       (response->status == KAIMO_LOCAL_STATUS_ERROR ||
		response->status == KAIMO_LOCAL_STATUS_OVERLOADED ||
		response->status == KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER))) ||
	    response->payload_length > payload_capacity) {
		close(fd);
		errno = EPROTO;
		return -2;
	}
	if (response->payload_length != 0 &&
	    kaimo_local_read_exact(fd, payload, response->payload_length) != 0) {
		int saved_errno = errno;
		close(fd);
		return saved_errno == EPROTO ? -2 : -1;
	}
	close(fd);
	return 0;
}

/* Sends a framed request to kaimo_authd and validates the authorization reply.
 * OPEN ALLOW replies carry the exact normalized/attenuated access mask.
 * Return: 1 = ALLOW, 0 = DENY, -1 = infrastructure/sidecar error,
 * -2 = malformed protocol response (always fail closed). */
static int kaimo_authz_send(uint8_t operation,
			    const struct kaimo_local_request *request,
			    uint32_t *granted_access)
{
	struct kaimo_local_frame_header response;
	uint8_t payload[4];
	int result = kaimo_roundtrip(operation, request, &response,
				     payload, sizeof(payload));
	if (result != 0)
		return result;

	if (response.status == KAIMO_LOCAL_STATUS_ALLOW) {
		if (operation == KAIMO_LOCAL_OP_OPEN) {
			struct kaimo_local_reader reader;
			uint32_t parsed;
			kaimo_local_reader_init(&reader, payload,
						response.payload_length);
			if (granted_access == NULL ||
			    !kaimo_local_reader_u32(&reader, &parsed) ||
			    !kaimo_local_reader_finished(&reader) ||
			    (parsed & ~KAIMO_SAMBA_SPECIFIC_ACCESS) != 0)
				return -2;
			*granted_access = parsed;
		} else if (response.payload_length != 0) {
			return -2;
		}
		return 1;
	}
	if (response.status == KAIMO_LOCAL_STATUS_DENY &&
	    response.payload_length == 0)
		return 0;
	if ((response.status == KAIMO_LOCAL_STATUS_ERROR ||
	     response.status == KAIMO_LOCAL_STATUS_OVERLOADED) &&
	    response.payload_length == 0)
		return -1;
	if (response.status == KAIMO_LOCAL_STATUS_UNAUTHORIZED_PEER &&
	    response.payload_length == 0)
		return -2;
	return -2;
}

static bool kaimo_authz_connect(const char *service, const char *user)
{
	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, user ? user : "");
	kaimo_local_builder_string(&request.builder, service ? service : "");
	if (!kaimo_request_ready("CONNECT", &request))
		return false;

	int d = kaimo_authz_send(KAIMO_LOCAL_OP_CONNECT, &request, NULL);
	if (d == -2) {
		DBG_ERR("kaimo_bridge: malformed CONNECT authorization response, denied\n");
		return false;
	}
	if (d < 0) {
		DBG_WARNING("kaimo_bridge: authd unreachable (connect), fail-%s\n",
			    kaimo_failmode_allow() ? "open" : "closed");
		return kaimo_failmode_allow();
	}
	return d == 1;
}

static bool kaimo_authz_open(const char *user, const char *share, const char *path,
			     uint32_t requested_access, bool wants_create,
			     bool create_directory, bool directory_listing,
			     uint32_t *granted_access)
{
	if (granted_access == NULL) {
		errno = EINVAL;
		return false;
	}
	/* A configured infrastructure fail-open must preserve Samba's original
	 * requested mask. A valid ALLOW reply replaces it with the server's exact
	 * normalized/attenuated mask. */
	*granted_access = requested_access;

	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, user ? user : "");
	kaimo_local_builder_string(&request.builder, share ? share : "");
	kaimo_local_builder_u32(&request.builder, requested_access);
	kaimo_local_builder_u8(&request.builder, wants_create ? 1 : 0);
	kaimo_local_builder_u8(&request.builder, create_directory ? 1 : 0);
	kaimo_local_builder_u8(&request.builder, directory_listing ? 1 : 0);
	kaimo_local_builder_string(&request.builder, path ? path : "");
	if (!kaimo_request_ready("OPEN", &request))
		return false;

	int d = kaimo_authz_send(KAIMO_LOCAL_OP_OPEN, &request,
				 granted_access);
	if (d == -2) {
		DBG_ERR("kaimo_bridge: malformed OPEN authorization response, denied\n");
		return false;
	}
	if (d < 0) return kaimo_failmode_allow();
	return d == 1;
}

static bool kaimo_authz_delete(const char *user, const char *share,
			       const char *path, bool is_directory)
{
	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, user ? user : "");
	kaimo_local_builder_string(&request.builder, share ? share : "");
	kaimo_local_builder_u8(&request.builder, is_directory ? 1 : 0);
	kaimo_local_builder_string(&request.builder, path ? path : "");
	if (!kaimo_request_ready("DELETEAUTH", &request))
		return false;

	int d = kaimo_authz_send(KAIMO_LOCAL_OP_DELETE_AUTH, &request, NULL);
	if (d == -2) {
		DBG_ERR("kaimo_bridge: malformed DELETE authorization response, denied\n");
		return false;
	}
	if (d < 0) {
		DBG_WARNING("kaimo_bridge: authd unreachable (delete), fail-%s\n",
			    kaimo_failmode_allow() ? "open" : "closed");
		return kaimo_failmode_allow();
	}
	return d == 1;
}

static bool kaimo_authz_rename(const char *user, const char *share,
			       const char *source_path,
			       const char *destination_path,
			       bool source_is_directory,
			       bool destination_exists,
			       bool destination_is_directory)
{
	/* Samba calls renameat only after it has accepted replacement of an
	 * existing destination. At this boundary destination_exists therefore
	 * also describes the effective replacement intent. */
	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, user ? user : "");
	kaimo_local_builder_string(&request.builder, share ? share : "");
	kaimo_local_builder_u8(&request.builder,
			       source_is_directory ? 1 : 0);
	kaimo_local_builder_u8(&request.builder,
			       destination_exists ? 1 : 0);
	kaimo_local_builder_u8(&request.builder,
			       destination_is_directory ? 1 : 0);
	kaimo_local_builder_u8(&request.builder,
			       destination_exists ? 1 : 0);
	kaimo_local_builder_string(&request.builder,
				   source_path ? source_path : "");
	kaimo_local_builder_string(&request.builder,
				   destination_path ? destination_path : "");
	if (!kaimo_request_ready("RENAMEAUTH", &request))
		return false;

	int d = kaimo_authz_send(KAIMO_LOCAL_OP_RENAME_AUTH, &request, NULL);
	if (d == -2) {
		DBG_ERR("kaimo_bridge: malformed RENAME authorization response, denied\n");
		return false;
	}
	if (d < 0) {
		DBG_WARNING("kaimo_bridge: authd unreachable (rename), fail-%s\n",
			    kaimo_failmode_allow() ? "open" : "closed");
		return kaimo_failmode_allow();
	}
	return d == 1;
}

/* Read an object relative to the exact directory handle Samba will pass to
 * renameat. ENOENT is a valid "missing destination" state; every other error
 * is surfaced so authorization cannot proceed on unknown filesystem state. */
static int kaimo_rename_stat(vfs_handle_struct *handle,
			     const struct files_struct *dirfsp,
			     const struct smb_filename *smb_fname,
			     SMB_STRUCT_STAT *st,
			     bool *exists)
{
	ZERO_STRUCTP(st);
	*exists = false;
	if (SMB_VFS_NEXT_FSTATAT(handle, dirfsp, smb_fname, st,
				 AT_SYMLINK_NOFOLLOW) == 0) {
		*exists = true;
		return 0;
	}
	if (errno == ENOENT)
		return 0;
	return -1;
}

static bool kaimo_list_filter_enabled(void)
{
	const char *v = getenv("KAIMO_LIST_FILTER");
	return !(v != NULL && v[0] == '0'); /* default: on */
}

/* Fire-and-forget notification to kaimo_authd (no response expected by this
 * caller), so SMB Close/Delete/Rename doesn't wait for gRPC processing. */
static void kaimo_notify_send(
	uint8_t operation, const struct kaimo_local_request *request)
{
	int fd = kaimo_authd_connect();
	if (fd < 0)
		return;
	(void)kaimo_local_send_frame(
		fd, operation, KAIMO_LOCAL_KIND_REQUEST,
		KAIMO_LOCAL_STATUS_NONE, request->payload,
		request->builder.length);
	close(fd);
}

/* ---- Phase 5: snapshot helpers (@GMT / "Previous Versions") ---- */

/* Snapshot replies may be larger than authorization replies, but their framed
 * payload is still explicitly bounded before reading into caller storage. */
static int kaimo_snap_request(
	uint8_t operation, const struct kaimo_local_request *request,
	struct kaimo_local_frame_header *response, uint8_t *payload,
	size_t payload_capacity)
{
	int result = kaimo_roundtrip(
		operation, request, response, payload, payload_capacity);
	if (result != 0)
		return result;
	if ((response->status == KAIMO_LOCAL_STATUS_ERROR ||
	     response->status == KAIMO_LOCAL_STATUS_OVERLOADED) &&
	    response->payload_length == 0)
		return -1;
	return 0;
}

/* Converts an SMB timewarp token (NTTIME) to the Windows label
 * "@GMT-YYYY.MM.DD-HH.MM.SS" (24 chars). out must hold >= 25 bytes. */
static bool kaimo_twrp_to_gmt(NTTIME twrp, char *out, size_t out_sz)
{
	time_t t = nt_time_to_unix(twrp);
	struct tm tmv;
	if (gmtime_r(&t, &tmv) == NULL) return false;
	return strftime(out, out_sz, "@GMT-%Y.%m.%d-%H.%M.%S", &tmv) == 24;
}

/* Kill-switch for the openat-based snapshot data-path redirect (default on).
 * KAIMO_SNAPSHOT_OPENAT=0 falls back to the (insufficient) create_file rewrite. */
static bool kaimo_snapshot_openat_enabled(void)
{
	const char *v = getenv("KAIMO_SNAPSHOT_OPENAT");
	return !(v != NULL && v[0] == '0');
}

/* Ask the bridge to materialize <gmt>:<logical> into the isolated snapshot cache and
 * return the cache-root-relative path in out.
 *   1  = ok (out = cache path)
 *   0  = no such version
 *  -1  = infrastructure error */
static int kaimo_snapresolve_rel(struct kaimo_conn_ctx *ctx, const char *gmt,
				 const char *logical, char *out, size_t outsz)
{
	if (ctx == NULL || gmt == NULL || logical == NULL || out == NULL || outsz == 0)
		return -1;

	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, ctx->user);
	kaimo_local_builder_string(&request.builder, ctx->share);
	kaimo_local_builder_string(&request.builder, gmt);
	kaimo_local_builder_string(&request.builder, logical);
	if (!kaimo_request_ready("SNAPRESOLVE", &request))
		return -1;

	uint8_t payload[KAIMO_LOCAL_MAX_REQUEST_PAYLOAD];
	struct kaimo_local_frame_header response;
	int result = kaimo_snap_request(
		KAIMO_LOCAL_OP_SNAPSHOT_RESOLVE, &request, &response,
		payload, sizeof(payload));
	if (result != 0)
		return -1;
	if (response.status == KAIMO_LOCAL_STATUS_NOT_FOUND &&
	    response.payload_length == 0)
		return 0;
	if (response.status != KAIMO_LOCAL_STATUS_OK)
		return -1;

	struct kaimo_local_reader reader;
	const uint8_t *cache_path;
	uint32_t cache_path_length;
	uint64_t version_size;
	kaimo_local_reader_init(&reader, payload, response.payload_length);
	if (!kaimo_local_reader_string(
	     &reader, &cache_path, &cache_path_length) ||
	    !kaimo_local_reader_u64(&reader, &version_size) ||
	    !kaimo_local_reader_finished(&reader) ||
	    cache_path_length == 0)
		return -1;
	(void)version_size;

	if ((size_t)cache_path_length >= outsz) {
		DBG_WARNING("kaimo_bridge: SNAPRESOLVE cache path too large, denied\n");
		errno = ENAMETOOLONG;
		return -1;
	}
	memcpy(out, cache_path, cache_path_length);
	out[cache_path_length] = '\0';
	return 1;
}

/* Make a path SHARE-RELATIVE for the bridge: some Samba call sites hand us the
 * absolute connectpath-prefixed path ("/data/storage/<share>/dir/file"), others the
 * already-relative one ("dir/file"). Strip the connectpath prefix if present, and
 * normalize a lone "." (share root) to "". MUST be applied identically in every
 * hook (openat AND stat/lstat) or their snapshot views diverge and Samba rejects the
 * open on the stat-vs-fd inode mismatch. */
static const char *kaimo_share_rel(vfs_handle_struct *handle, const char *path)
{
	if (path == NULL) return "";
	const char *cp = handle->conn->connectpath;
	size_t cplen = (cp != NULL) ? strlen(cp) : 0;
	while (cplen > 1 && cp[cplen - 1] == '/') cplen--;
	/* A byte-prefix alone is insufficient: connectpath=/share must not strip
	 * the unrelated absolute path /share-backup/file. */
	if (cplen > 0 && strncmp(path, cp, cplen) == 0 &&
	    (path[cplen] == '\0' || path[cplen] == '/')) {
		path += cplen;
		while (*path == '/') path++;
	}
	if (path[0] == '.' && path[1] == '\0') path++; /* "." -> "" */
	return path;
}

#define KAIMO_LEGACY_CACHE_DIR ".kaimo-snapshots"
#define KAIMO_DEFAULT_SNAPSHOT_CACHE_ROOT "/data/storage/.kaimo-snapshots"

/* The old in-share cache name stays permanently reserved. This blocks direct
 * client access to stale pre-P0-06 materializations during rolling upgrades and
 * prevents clients from planting a lookalike internal namespace. */
static bool kaimo_is_reserved_client_path(vfs_handle_struct *handle,
					  const char *path)
{
	const char *logical = kaimo_share_rel(handle, path);
	while (logical[0] == '.' && logical[1] == '/') logical += 2;
	while (logical[0] == '/') logical++;
	size_t reserved_len = strlen(KAIMO_LEGACY_CACHE_DIR);
	return strnequal(logical, KAIMO_LEGACY_CACHE_DIR, reserved_len) &&
	       (logical[reserved_len] == '\0' || logical[reserved_len] == '/');
}

/* The bridge response crosses an unauthenticated control-plane boundary today,
 * so never treat it as an arbitrary path. Only non-empty relative paths without
 * empty/dot components are accepted before the fixed local cache root is joined. */
static bool kaimo_cache_relative_path_valid(const char *path)
{
	if (path == NULL || path[0] == '\0' || path[0] == '/' ||
	    strchr(path, '\\') != NULL)
		return false;
	for (const unsigned char *p = (const unsigned char *)path; *p != '\0'; p++) {
		if (*p < 0x20 || *p == 0x7f) return false;
	}

	const char *component = path;
	for (;;) {
		const char *slash = strchr(component, '/');
		size_t len = slash != NULL
			? (size_t)(slash - component) : strlen(component);
		if (len == 0 || (len == 1 && component[0] == '.') ||
		    (len == 2 && component[0] == '.' && component[1] == '.'))
			return false;
		if (slash == NULL) return true;
		component = slash + 1;
	}
}

static char *kaimo_snapshot_cache_abspath(TALLOC_CTX *mem_ctx,
					  const char *relative)
{
	const char *root = getenv("KAIMO_SNAPSHOT_CACHE_ROOT");
	if (root == NULL || root[0] == '\0')
		root = KAIMO_DEFAULT_SNAPSHOT_CACHE_ROOT;
	if (root[0] != '/' || !kaimo_cache_relative_path_valid(relative)) {
		DBG_ERR("kaimo_bridge: invalid snapshot cache root/relative path denied\n");
		errno = EACCES;
		return NULL;
	}

	size_t root_len = strlen(root);
	while (root_len > 1 && root[root_len - 1] == '/') root_len--;
	char *trimmed_root = talloc_strndup(mem_ctx, root, root_len);
	if (trimmed_root == NULL) {
		errno = ENOMEM;
		return NULL;
	}
	return talloc_asprintf(mem_ctx, "%s/%s", trimmed_root, relative);
}

/* If smb_fname carries a timewarp (twrp != 0), resolve it to the isolated version
 * copy and rewrite base_name to that copy (used by the path-based stat/lstat hooks,
 * which have no fd). The actual data-path open is redirected in kaimo_openat.
 *   1  = rewritten to a snapshot copy (bridge already enforced read ACL)
 *   0  = no twrp, nothing to do
 *  -1  = twrp set but version not found / infrastructure error (fail the op) */
static int kaimo_apply_twrp(vfs_handle_struct *handle, struct smb_filename *smb_fname)
{
	if (smb_fname == NULL || smb_fname->twrp == 0) return 0;
	errno = 0;

	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	if (ctx == NULL || smb_fname->base_name == NULL) return -1;

	char gmt[32];
	if (!kaimo_twrp_to_gmt(smb_fname->twrp, gmt, sizeof(gmt))) return -1;

	const char *logical = kaimo_share_rel(handle, smb_fname->base_name);
	char cache[6144];
	errno = 0;
	if (kaimo_snapresolve_rel(ctx, gmt, logical, cache, sizeof(cache)) != 1)
		return -1;

	char *newname = kaimo_snapshot_cache_abspath(smb_fname, cache);
	if (newname == NULL) return -1;
	smb_fname->base_name = newname;
	smb_fname->twrp = 0; /* handled -> ordinary file for NEXT_* */
	DBG_INFO("kaimo_bridge: SNAPSHOT resolve [%s] -> [%s]\n", gmt, newname);
	return 1;
}

/* FSCTL_SRV_ENUMERATE_SNAPSHOTS: return the @GMT- tokens available for this file.
 * The bridge maps to IFileVersionService.GetSnapshotTimestamps / GetVersions.
 * Signature matches vfs.h: returns int (0 = ok, -1 + errno on failure). The label
 * array is talloc'd off the shadow_copy_data object itself (as vfs_shadow_copy2). */
static int kaimo_get_shadow_copy_data(vfs_handle_struct *handle,
				      struct files_struct *fsp,
				      struct shadow_copy_data *shadow_copy_data,
				      bool labels)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	const char *path = (fsp != NULL && fsp->fsp_name != NULL &&
			    fsp->fsp_name->base_name != NULL)
				? fsp->fsp_name->base_name : "";
	const char *logical = kaimo_share_rel(handle, path);

	shadow_copy_data->num_volumes = 0;
	shadow_copy_data->labels = NULL;
	if (ctx == NULL) return 0;
	if (kaimo_is_reserved_client_path(handle, path)) {
		errno = EACCES;
		return -1;
	}

	struct kaimo_local_request request;
	kaimo_request_init(&request);
	kaimo_local_builder_string(&request.builder, ctx->user);
	kaimo_local_builder_string(&request.builder, ctx->share);
	kaimo_local_builder_string(&request.builder, logical);
	if (!kaimo_request_ready("SNAPENUM", &request))
		return -1;

	uint8_t payload[KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD];
	struct kaimo_local_frame_header response;
	int result = kaimo_snap_request(
		KAIMO_LOCAL_OP_SNAPSHOT_ENUMERATE, &request, &response,
		payload, sizeof(payload));
	if (result != 0) {
		/* Infrastructure down -> report "no snapshots" rather than failing
		 * the whole Properties dialog. */
		DBG_WARNING("kaimo_bridge: SNAPENUM authd unreachable\n");
		return 0;
	}
	if (response.status != KAIMO_LOCAL_STATUS_OK)
		return 0;

	struct kaimo_local_reader reader;
	uint32_t count;
	kaimo_local_reader_init(&reader, payload, response.payload_length);
	if (!kaimo_local_reader_u32(&reader, &count) ||
	    count > INT_MAX ||
	    count > (response.payload_length - reader.offset) / 4) {
		DBG_WARNING("kaimo_bridge: malformed SNAPENUM count, ignored\n");
		return 0;
	}
	if (count == 0 && kaimo_local_reader_finished(&reader))
		return 0;

	SHADOW_COPY_LABEL *lbl = NULL;
	if (labels) {
		lbl = talloc_zero_array(shadow_copy_data,
					SHADOW_COPY_LABEL, count);
		if (lbl == NULL) {
			errno = ENOMEM;
			return -1;
		}
	}

	for (uint32_t i = 0; i < count; ++i) {
		const uint8_t *token;
		uint32_t token_length;
		if (!kaimo_local_reader_string(
		     &reader, &token, &token_length) ||
		    token_length >= sizeof(SHADOW_COPY_LABEL)) {
			DBG_WARNING("kaimo_bridge: malformed SNAPENUM token, ignored\n");
			TALLOC_FREE(lbl);
			return 0;
		}
		if (labels) {
			memcpy(lbl[i], token, token_length);
			lbl[i][token_length] = '\0';
		}
	}
	if (!kaimo_local_reader_finished(&reader)) {
		DBG_WARNING("kaimo_bridge: trailing SNAPENUM payload, ignored\n");
		TALLOC_FREE(lbl);
		return 0;
	}

	shadow_copy_data->num_volumes = (int)count;
	shadow_copy_data->labels = lbl;
	DBG_INFO("kaimo_bridge: SNAPENUM path=[%s] -> %u labels\n",
		 logical, count);
	return 0;
}

/* ---- Snapshot-aware stat/lstat: resolve a timewarp path to its version copy ---- */
static int kaimo_stat(vfs_handle_struct *handle, struct smb_filename *smb_fname)
{
	if (smb_fname != NULL &&
	    kaimo_is_reserved_client_path(handle, smb_fname->base_name)) {
		errno = EACCES;
		return -1;
	}
	if (kaimo_apply_twrp(handle, smb_fname) < 0) {
		if (errno != ENOMEM && errno != ENAMETOOLONG) errno = ENOENT;
		return -1;
	}
	return SMB_VFS_NEXT_STAT(handle, smb_fname);
}

static int kaimo_lstat(vfs_handle_struct *handle, struct smb_filename *smb_fname)
{
	if (smb_fname != NULL &&
	    kaimo_is_reserved_client_path(handle, smb_fname->base_name)) {
		errno = EACCES;
		return -1;
	}
	if (kaimo_apply_twrp(handle, smb_fname) < 0) {
		if (errno != ENOMEM && errno != ENAMETOOLONG) errno = ENOENT;
		return -1;
	}
	return SMB_VFS_NEXT_LSTAT(handle, smb_fname);
}

/* Builds the complete path from directory-fsp + at-relative name without a
 * fixed buffer. The caller then canonicalizes it through kaimo_share_rel(). */
static char *kaimo_join_path(TALLOC_CTX *mem_ctx,
			     const struct files_struct *dirfsp,
			     const struct smb_filename *name)
{
	const char *dir = (dirfsp != NULL && dirfsp->fsp_name != NULL)
				? dirfsp->fsp_name->base_name : NULL;
	const char *leaf = (name != NULL && name->base_name != NULL)
				? name->base_name : "";
	bool root = (dir == NULL) || dir[0] == '\0'
			|| (dir[0] == '.' && dir[1] == '\0');
	if (root)
		return talloc_strdup(mem_ctx, leaf);
	return talloc_asprintf(mem_ctx, "%s/%s", dir, leaf);
}

/* ---- TREE_CONNECT: authorize + store context (user/share) in handle ---- */
static int kaimo_connect(vfs_handle_struct *handle,
			 const char *service,
			 const char *user)
{
	bool is_ipc = (service != NULL && strequal(service, "IPC$"));
	struct kaimo_conn_ctx *ctx = NULL;

	if (!is_ipc && !kaimo_authz_connect(service, user)) {
		DBG_ERR("kaimo_bridge: CONNECT DENIED share=[%s] user=[%s]\n",
			service ? service : "(null)", user ? user : "(null)");
		errno = EACCES;
		return -1;
	}

	DBG_ERR("kaimo_bridge: CONNECT ALLOW share=[%s] user=[%s]\n",
		service ? service : "(null)", user ? user : "(null)");

	/* Prepare the mandatory authorization context BEFORE connecting the next
	 * VFS layer. If allocation fails, no usable share connection may exist:
	 * create_file/readdir depend on this context for their ACL decisions and
	 * treating OOM as "no context" would otherwise become a fail-open path.
	 * IPC$ deliberately has no file-authorization context. */
	if (!is_ipc) {
		ctx = calloc(1, sizeof(*ctx));
		if (ctx == NULL) {
			DBG_ERR("kaimo_bridge: CONNECT context allocation failed "
				"share=[%s] user=[%s]\n",
				service ? service : "(null)",
				user ? user : "(null)");
			errno = ENOMEM;
			return -1;
		}

		strlcpy(ctx->user, user ? user : "", sizeof(ctx->user));
		strlcpy(ctx->share, service ? service : "", sizeof(ctx->share));
	}

	int ret = SMB_VFS_NEXT_CONNECT(handle, service, user);
	if (ret < 0) {
		free(ctx);
		return ret;
	}

	if (!is_ipc) {
		SMB_VFS_HANDLE_SET_DATA(handle, ctx, kaimo_free_data,
					struct kaimo_conn_ctx, { free(ctx); });
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
	if (smb_fname != NULL &&
	    kaimo_is_reserved_client_path(handle, smb_fname->base_name)) {
		DBG_WARNING("kaimo_bridge: reserved snapshot namespace denied [%s]\n",
			smb_fname->base_name != NULL ? smb_fname->base_name : "");
		return NT_STATUS_ACCESS_DENIED;
	}

	/* Phase 5: a timewarp open ("Previous Versions"). ResolveVersion enforces
	 * read ACL, and P0-03 additionally maps/attenuates the complete requested
	 * access mask here. IMPORTANT: keep twrp INTACT and do NOT rewrite base_name
	 * here — Samba opens the
	 * real fd via openat (relative to a parent dirfsp), not via this base_name, so the
	 * redirect to the version copy must happen in kaimo_openat. Rewriting here is
	 * ignored for file content (it opens the live file). */
	if (smb_fname != NULL && smb_fname->twrp != 0) {
		uint32_t granted_access = 0;
		const char *logical = kaimo_share_rel(handle,
						      smb_fname->base_name);
		if (ctx == NULL || smb_fname->base_name == NULL ||
		    !kaimo_authz_open(ctx->user, ctx->share, logical,
				      access_mask, false, false, false,
				      &granted_access)) {
			DBG_ERR("kaimo_bridge: CREATE twrp DENIED path=[%s]\n",
				logical);
			return NT_STATUS_ACCESS_DENIED;
		}
		access_mask = granted_access;
		NTSTATUS tst = SMB_VFS_NEXT_CREATE_FILE(handle, req, dirfsp, smb_fname, access_mask,
					       share_access, create_disposition, create_options,
					       file_attributes, oplock_request, lease,
					       allocation_size, private_flags, sd, ea_list,
					       result, pinfo, in_context_blobs, out_context_blobs);
		DBG_INFO("kaimo_bridge: CREATE twrp [%s] access=0x%08x share=0x%x disp=%u opts=0x%x -> %s\n",
			 smb_fname->base_name, (unsigned)access_mask, (unsigned)share_access,
			 (unsigned)create_disposition, (unsigned)create_options, nt_errstr(tst));
		return tst;
	}

	if (ctx != NULL && smb_fname != NULL && smb_fname->base_name != NULL) {
		const char *logical = kaimo_share_rel(handle,
						      smb_fname->base_name);
		bool wants_create =
			(create_disposition == FILE_SUPERSEDE ||
			 create_disposition == FILE_CREATE ||
			 create_disposition == FILE_OPEN_IF ||
			 create_disposition == FILE_OVERWRITE_IF);
		bool create_directory =
			(create_options & FILE_DIRECTORY_FILE) != 0;
		uint32_t granted_access = 0;

		if (!kaimo_authz_open(ctx->user, ctx->share, logical,
				      access_mask, wants_create, create_directory,
				      false,
				      &granted_access)) {
			DBG_ERR("kaimo_bridge: CREATE DENIED path=[%s] user=[%s]\n",
				logical, ctx->user);
			return NT_STATUS_ACCESS_DENIED;
		}

		/* Do not let Samba's broad POSIX identity recover rights that Kaimo
		 * removed from a MAXIMUM_ALLOWED request. Generic bits are also
		 * replaced by the exact specific mask returned by the control plane. */
		access_mask = granted_access;
	}

	NTSTATUS status = SMB_VFS_NEXT_CREATE_FILE(handle, req, dirfsp, smb_fname, access_mask,
				       share_access, create_disposition, create_options,
				       file_attributes, oplock_request, lease,
				       allocation_size, private_flags, sd, ea_list,
				       result, pinfo, in_context_blobs, out_context_blobs);

	/* Reliable directory-create indexing (backlog #8): Samba's SMB2 dir-create
	 * path does not reliably route through mkdirat, so a folder made over SMB2
	 * could miss versioning/ownership/search-index stamping. When create_file
	 * itself CREATED a directory, emit the MKDIR event here. The mkdirat hook
	 * stays as a fallback and NotifyMkdir is idempotent, so a possible double
	 * notification is harmless. */
	if (NT_STATUS_IS_OK(status) && ctx != NULL &&
	    pinfo != NULL && *pinfo == FILE_WAS_CREATED &&
	    result != NULL && *result != NULL &&
	    (*result)->fsp_flags.is_directory &&
	    smb_fname != NULL && smb_fname->base_name != NULL &&
	    smb_fname->base_name[0] != '\0') {
		TALLOC_CTX *frame = talloc_stackframe();
		const char *logical = kaimo_share_rel(handle,
						      smb_fname->base_name);
		struct kaimo_local_request request;
		kaimo_request_init(&request);
		kaimo_local_builder_string(&request.builder, ctx->user);
		kaimo_local_builder_string(&request.builder, ctx->share);
		kaimo_local_builder_string(&request.builder, logical);
		if (kaimo_request_ready("MKDIR", &request))
			kaimo_notify_send(KAIMO_LOCAL_OP_MKDIR, &request);
		TALLOC_FREE(frame);
	}

	return status;
}

/* ---- Directory listing filter: hide entries without read permission (ListAsync) ---- */
static struct dirent *kaimo_readdir(vfs_handle_struct *handle,
				    struct files_struct *dirfsp,
				    DIR *dirp)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	const char *dirpath = (dirfsp != NULL && dirfsp->fsp_name != NULL)
				? dirfsp->fsp_name->base_name : NULL;
	const char *logical_dir = (dirpath != NULL)
				? kaimo_share_rel(handle, dirpath) : NULL;
	if (dirpath != NULL && kaimo_is_reserved_client_path(handle, dirpath)) {
		errno = EACCES;
		return NULL;
	}
	bool filter = kaimo_list_filter_enabled() && ctx != NULL && dirpath != NULL;
	bool at_root = (logical_dir != NULL && logical_dir[0] == '\0');

	for (;;) {
		struct dirent *e = SMB_VFS_NEXT_READDIR(handle, dirfsp, dirp);
		if (e == NULL) return NULL;
		if (!filter) return e;

		const char *nm = e->d_name;
		/* Always allow "." and ".." */
		if (nm[0] == '.' && (nm[1] == '\0' || (nm[1] == '.' && nm[2] == '\0')))
			return e;

		/* Hide reserved Kaimo internal names. Access to the legacy cache name is
		 * independently rejected by all path-bearing hooks. */
		if (strncmp(nm, ".kaimo-", 7) == 0)
			continue;

		TALLOC_CTX *frame = talloc_stackframe();
		char *path = at_root
			? talloc_strdup(frame, nm)
			: talloc_asprintf(frame, "%s/%s", logical_dir, nm);
		if (path == NULL) {
			DBG_ERR("kaimo_bridge: LIST path allocation failed\n");
			TALLOC_FREE(frame);
			errno = ENOMEM;
			return NULL;
		}

		uint32_t granted_access = 0;
		if (kaimo_authz_open(ctx->user, ctx->share, path,
				     SEC_FILE_READ_DATA, false, false, true,
				     &granted_access)) {
			TALLOC_FREE(frame);
			return e; /* readable -> show */
		}

		DBG_INFO("kaimo_bridge: LIST hide [%s] user=[%s]\n", path, ctx->user);
		TALLOC_FREE(frame);
		/* not readable -> skip, get next entry */
	}
}

/* ---- Close hook: written file -> versioning/index/ownership (Phase 3) ---- */
static int kaimo_close(vfs_handle_struct *handle, files_struct *fsp)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	bool modified = (fsp != NULL) && fsp->fsp_flags.modified;
	bool isdir = (fsp != NULL) && fsp->fsp_flags.is_directory;

	/* The fsp may no longer be valid after NEXT_CLOSE, so preserve the exact
	 * canonical path before closing. Closing must still proceed on OOM to avoid
	 * leaking the native descriptor; only the best-effort event is then lost. */
	TALLOC_CTX *frame = talloc_stackframe();
	char *path = NULL;
	struct kaimo_local_request event_request;
	kaimo_request_init(&event_request);
	bool event_ready = false;
	if (fsp != NULL && fsp->fsp_name != NULL && fsp->fsp_name->base_name != NULL)
		path = talloc_strdup(frame,
				     kaimo_share_rel(handle,
						     fsp->fsp_name->base_name));
	if (modified && !isdir && path == NULL)
		DBG_WARNING("kaimo_bridge: CLOSE path unavailable; "
			    "native close will still proceed\n");
	if (ctx != NULL && modified && !isdir && path != NULL && path[0] != '\0') {
		kaimo_local_builder_string(&event_request.builder, ctx->user);
		kaimo_local_builder_string(&event_request.builder, ctx->share);
		kaimo_local_builder_string(&event_request.builder, path);
		event_ready = kaimo_request_ready("CLOSE", &event_request);
	}

	int ret = SMB_VFS_NEXT_CLOSE(handle, fsp);

	if (ret == 0 && event_ready)
		kaimo_notify_send(KAIMO_LOCAL_OP_CLOSE, &event_request);
	TALLOC_FREE(frame);
	return ret;
}

/* ---- Delete hook: clean up search index (Phase 3) ---- */
static int kaimo_unlinkat(vfs_handle_struct *handle,
			  struct files_struct *srcdir_fsp,
			  const struct smb_filename *smb_fname,
			  int flags)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	TALLOC_CTX *frame = talloc_stackframe();
	char *path = kaimo_join_path(frame, srcdir_fsp, smb_fname);
	if (path == NULL) {
		TALLOC_FREE(frame);
		errno = ENOMEM;
		return -1;
	}
	bool isdir = (flags & AT_REMOVEDIR) != 0;
	const char *logical = kaimo_share_rel(handle, path);
	if (kaimo_is_reserved_client_path(handle, path)) {
		TALLOC_FREE(frame);
		errno = EACCES;
		return -1;
	}

	/* Authorization must happen before the native unlink/rmdir. The DELETE event
	 * below is only post-operation bookkeeping and cannot protect the data path. */
	errno = 0;
	if (ctx == NULL || !kaimo_authz_delete(ctx->user, ctx->share, logical, isdir)) {
		int auth_errno = errno;
		DBG_ERR("kaimo_bridge: DELETE DENIED path=[%s] user=[%s]\n",
			logical, ctx != NULL ? ctx->user : "");
		TALLOC_FREE(frame);
		errno = (auth_errno == ENOMEM || auth_errno == ENAMETOOLONG)
			? auth_errno : EACCES;
		return -1;
	}

	/* Build and validate the exact post-operation event before mutation. This
	 * ensures a locally unrepresentable path cannot be deleted successfully
	 * after only a truncated lifecycle path was recorded. */
	struct kaimo_local_request event_request;
	kaimo_request_init(&event_request);
	kaimo_local_builder_string(&event_request.builder, ctx->user);
	kaimo_local_builder_string(&event_request.builder, ctx->share);
	kaimo_local_builder_u8(&event_request.builder, isdir ? 1 : 0);
	kaimo_local_builder_string(&event_request.builder, logical);
	if (!kaimo_request_ready("DELETE", &event_request)) {
		TALLOC_FREE(frame);
		return -1;
	}

	int ret = SMB_VFS_NEXT_UNLINKAT(handle, srcdir_fsp, smb_fname, flags);

	if (ret == 0 && logical[0] != '\0')
		kaimo_notify_send(KAIMO_LOCAL_OP_DELETE, &event_request);
	TALLOC_FREE(frame);
	return ret;
}

/* ---- Rename hook: pre-op ACL + search index follow-up (Phase 3/P0-04) ---- */
static int kaimo_renameat(vfs_handle_struct *handle,
			  struct files_struct *srcdir_fsp,
			  const struct smb_filename *smb_fname_src,
			  struct files_struct *dstdir_fsp,
			  const struct smb_filename *smb_fname_dst)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	TALLOC_CTX *frame = talloc_stackframe();
	char *oldp = kaimo_join_path(frame, srcdir_fsp, smb_fname_src);
	char *newp = kaimo_join_path(frame, dstdir_fsp, smb_fname_dst);
	if (ctx == NULL || oldp == NULL || newp == NULL) {
		TALLOC_FREE(frame);
		errno = (ctx == NULL) ? EACCES : ENOMEM;
		return -1;
	}
	const char *oldlogical = kaimo_share_rel(handle, oldp);
	const char *newlogical = kaimo_share_rel(handle, newp);
	if (kaimo_is_reserved_client_path(handle, oldp) ||
	    kaimo_is_reserved_client_path(handle, newp)) {
		TALLOC_FREE(frame);
		errno = EACCES;
		return -1;
	}

	SMB_STRUCT_STAT source_before;
	SMB_STRUCT_STAT destination_before;
	bool source_exists = false;
	bool destination_exists = false;
	if (kaimo_rename_stat(handle, srcdir_fsp, smb_fname_src,
			      &source_before, &source_exists) != 0 ||
	    !source_exists ||
	    kaimo_rename_stat(handle, dstdir_fsp, smb_fname_dst,
			      &destination_before, &destination_exists) != 0) {
		int stat_errno = errno;
		DBG_ERR("kaimo_bridge: RENAME state inspection failed [%s] -> [%s]\n",
			oldlogical, newlogical);
		TALLOC_FREE(frame);
		errno = stat_errno != 0 ? stat_errno : ENOENT;
		return -1;
	}

	bool source_is_directory = S_ISDIR(source_before.st_ex_mode);
	bool destination_is_directory = destination_exists &&
		S_ISDIR(destination_before.st_ex_mode);

	/* P0-02 requires exact, prevalidated event paths before mutation. */
	struct kaimo_local_request event_request;
	kaimo_request_init(&event_request);
	kaimo_local_builder_string(&event_request.builder, ctx->user);
	kaimo_local_builder_string(&event_request.builder, ctx->share);
	kaimo_local_builder_u8(&event_request.builder,
			       source_is_directory ? 1 : 0);
	kaimo_local_builder_string(&event_request.builder, oldlogical);
	kaimo_local_builder_string(&event_request.builder, newlogical);
	if (!kaimo_request_ready("RENAME", &event_request)) {
		TALLOC_FREE(frame);
		return -1;
	}

	/* Authorize the complete move before touching the filesystem: source
	 * removal, destination-parent creation, and replacement deletion. */
	errno = 0;
	if (!kaimo_authz_rename(ctx->user, ctx->share,
				oldlogical, newlogical,
				source_is_directory,
				destination_exists,
				destination_is_directory)) {
		int auth_errno = errno;
		DBG_ERR("kaimo_bridge: RENAME DENIED [%s] -> [%s] user=[%s]\n",
			oldlogical, newlogical, ctx->user);
		TALLOC_FREE(frame);
		errno = (auth_errno == ENOMEM || auth_errno == ENAMETOOLONG)
			? auth_errno : EACCES;
		return -1;
	}

	/* Narrow the RPC TOCTOU window. If either directory entry disappeared,
	 * appeared, changed type, or was exchanged for another inode while the
	 * bridge decided, fail closed. renameat itself remains the final atomic
	 * operation; Samba 4.19.5 exposes no replace flag to this VFS hook. */
	SMB_STRUCT_STAT source_after;
	SMB_STRUCT_STAT destination_after;
	bool source_still_exists = false;
	bool destination_still_exists = false;
	if (kaimo_rename_stat(handle, srcdir_fsp, smb_fname_src,
			      &source_after, &source_still_exists) != 0 ||
	    kaimo_rename_stat(handle, dstdir_fsp, smb_fname_dst,
			      &destination_after,
			      &destination_still_exists) != 0 ||
	    !source_still_exists ||
	    destination_exists != destination_still_exists ||
	    !check_same_dev_ino(&source_before, &source_after) ||
	    S_ISDIR(source_before.st_ex_mode) !=
		S_ISDIR(source_after.st_ex_mode) ||
	    (destination_exists &&
	     (!check_same_dev_ino(&destination_before, &destination_after) ||
	      S_ISDIR(destination_before.st_ex_mode) !=
		S_ISDIR(destination_after.st_ex_mode)))) {
		DBG_WARNING("kaimo_bridge: RENAME state changed during authorization "
			    "[%s] -> [%s], retry required\n",
			    oldlogical, newlogical);
		TALLOC_FREE(frame);
		errno = EAGAIN;
		return -1;
	}

	int ret = SMB_VFS_NEXT_RENAMEAT(handle, srcdir_fsp, smb_fname_src,
					dstdir_fsp, smb_fname_dst);

	if (ret == 0 && oldlogical[0] != '\0' && newlogical[0] != '\0') {
		kaimo_notify_send(KAIMO_LOCAL_OP_RENAME, &event_request);
	}
	TALLOC_FREE(frame);
	return ret;
}

/* ---- Mkdir hook: index new directory + ownership (Phase 3) ----
 * Fallback path for directory creation; the primary, reliable notification for
 * SMB2 dir-create now happens in kaimo_create_file (backlog #8). NotifyMkdir is
 * idempotent, so both firing for one directory is harmless. */
static int kaimo_mkdirat(vfs_handle_struct *handle,
			 struct files_struct *dirfsp,
			 const struct smb_filename *smb_fname,
			 mode_t mode)
{
	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	TALLOC_CTX *frame = talloc_stackframe();
	char *path = kaimo_join_path(frame, dirfsp, smb_fname);
	if (ctx == NULL || path == NULL) {
		TALLOC_FREE(frame);
		errno = (ctx == NULL) ? EACCES : ENOMEM;
		return -1;
	}
	const char *logical = kaimo_share_rel(handle, path);
	if (kaimo_is_reserved_client_path(handle, path)) {
		TALLOC_FREE(frame);
		errno = EACCES;
		return -1;
	}
	struct kaimo_local_request event_request;
	kaimo_request_init(&event_request);
	kaimo_local_builder_string(&event_request.builder, ctx->user);
	kaimo_local_builder_string(&event_request.builder, ctx->share);
	kaimo_local_builder_string(&event_request.builder, logical);
	if (!kaimo_request_ready("MKDIR", &event_request)) {
		TALLOC_FREE(frame);
		return -1;
	}

	int ret = SMB_VFS_NEXT_MKDIRAT(handle, dirfsp, smb_fname, mode);

	if (ret == 0 && logical[0] != '\0')
		kaimo_notify_send(KAIMO_LOCAL_OP_MKDIR, &event_request);
	TALLOC_FREE(frame);
	return ret;
}

/* ---- openat: the data-path redirect for @GMT opens (Phase 5) ----
 * This is where Samba obtains the real file descriptor, relative to a parent
 * dirfsp — NOT via the create_file base_name. So a snapshot open must be redirected
 * HERE, or the live file is served (that was the "Previous Versions shows current
 * content / file not found" bug). Mirrors what Samba's own vfs_shadow_copy2 does.
 *
 * When smb_fname carries a timewarp we reconstruct the logical share-relative path
 * (parent fsp path + atname), ask the bridge to materialize that version into the
 * isolated cache, and open the cache copy by an internally constructed absolute
 * path. Ordinary opens also reject the reserved legacy cache namespace before
 * passing through. Kill-switch: KAIMO_SNAPSHOT_OPENAT=0. */
static int kaimo_openat(vfs_handle_struct *handle,
			const struct files_struct *dirfsp,
			const struct smb_filename *smb_fname,
			struct files_struct *fsp,
			const struct vfs_open_how *how)
{
	if (smb_fname == NULL)
		return SMB_VFS_NEXT_OPENAT(handle, dirfsp, smb_fname, fsp, how);

	TALLOC_CTX *frame = talloc_stackframe();
	char *joined = kaimo_join_path(frame, dirfsp, smb_fname);
	if (joined == NULL) {
		TALLOC_FREE(frame);
		errno = ENOMEM;
		return -1;
	}
	if (kaimo_is_reserved_client_path(handle, joined)) {
		TALLOC_FREE(frame);
		errno = EACCES;
		return -1;
	}
	if (smb_fname->twrp == 0 || !kaimo_snapshot_openat_enabled()) {
		TALLOC_FREE(frame);
		return SMB_VFS_NEXT_OPENAT(handle, dirfsp, smb_fname, fsp, how);
	}

	struct kaimo_conn_ctx *ctx = (struct kaimo_conn_ctx *)handle->data;
	if (ctx == NULL) {
		TALLOC_FREE(frame);
		errno = ENOENT;
		return -1;
	}

	/* "." / ".." refer to the directory itself / its parent. When the parent was
	 * already redirected, dirfsp's fd points into the snapshot cache, so opening
	 * these relative to dirfsp via NEXT lands in the cache — don't try to resolve
	 * them as version paths (that's the [topOrdner/.] / [topOrdner/..] failures). */
	const char *leaf = smb_fname->base_name;
	if (leaf != NULL && (strcmp(leaf, ".") == 0 || strcmp(leaf, "..") == 0)) {
		TALLOC_FREE(frame);
		return SMB_VFS_NEXT_OPENAT(handle, dirfsp, smb_fname, fsp, how);
	}

	char gmt[32];
	if (!kaimo_twrp_to_gmt(smb_fname->twrp, gmt, sizeof(gmt))) {
		TALLOC_FREE(frame);
		errno = ENOENT;
		return -1;
	}

	/* Reconstruct the path from parent + atname, then make it SHARE-RELATIVE:
	 * some openat calls (Samba's realpath / non-widelink verification) arrive with
	 * the parent fsp carrying the absolute connectpath, yielding an absolute path
	 * like "/data/storage/<share>/topOrdner". The bridge expects a share-relative
	 * path ("topOrdner"), so strip the connectpath prefix. A lone "." -> "" (root). */
	const char *logical = kaimo_share_rel(handle, joined);

	char cache_rel[6144];
	errno = 0;
	int rr = kaimo_snapresolve_rel(ctx, gmt, logical, cache_rel, sizeof(cache_rel));
	if (rr != 1) {
		int resolve_errno = errno;
		/* Expected for paths without a version at this snapshot (e.g. desktop.ini,
		 * thumbnails) — not an error, just "no such version". */
		DBG_INFO("kaimo_bridge: OPENAT no version [%s@%s]\n", logical, gmt);
		TALLOC_FREE(frame);
		errno = (resolve_errno == ENOMEM || resolve_errno == ENAMETOOLONG)
			? resolve_errno : ENOENT;
		return -1;
	}

	char *abspath = kaimo_snapshot_cache_abspath(frame, cache_rel);
	if (abspath == NULL) {
		int path_errno = errno;
		TALLOC_FREE(frame);
		errno = path_errno != 0 ? path_errno : ENOMEM;
		return -1;
	}

	int fd = openat(AT_FDCWD, abspath, how->flags, how->mode);
	if (fd < 0)
		DBG_ERR("kaimo_bridge: OPENAT SNAPSHOT [%s@%s] flags=0x%x -> FAILED errno=%d\n",
			logical, gmt, (unsigned)how->flags, errno);
	else
		DBG_INFO("kaimo_bridge: OPENAT SNAPSHOT [%s@%s] -> [%s] fd=%d\n",
			 logical, gmt, abspath, fd);
	TALLOC_FREE(frame);
	return fd;
}

static struct vfs_fn_pointers kaimo_bridge_fns = {
	.connect_fn     = kaimo_connect,
	.disconnect_fn  = kaimo_disconnect,
	.create_file_fn = kaimo_create_file,
	.openat_fn      = kaimo_openat,
	.readdir_fn     = kaimo_readdir,
	.close_fn       = kaimo_close,
	.unlinkat_fn    = kaimo_unlinkat,
	.renameat_fn    = kaimo_renameat,
	.mkdirat_fn     = kaimo_mkdirat,
	/* Phase 5: @GMT snapshots ("Previous Versions"). */
	.get_shadow_copy_data_fn = kaimo_get_shadow_copy_data,
	.stat_fn        = kaimo_stat,
	.lstat_fn       = kaimo_lstat,
};

/* Build marker: bump on every module change so the running image can be
 * identified in the logs (grep "kaimo_bridge build"). This is how we tell whether
 * a rebuild actually picked up the latest source vs. served a cached layer. */
#define KAIMO_BRIDGE_BUILD "2026-07-23e authenticated local peer"

static_decl_vfs;
NTSTATUS vfs_kaimo_bridge_init(TALLOC_CTX *ctx)
{
	DBG_NOTICE("kaimo_bridge build [%s] loaded\n", KAIMO_BRIDGE_BUILD);
	return smb_register_vfs(SMB_VFS_INTERFACE_VERSION, "kaimo_bridge",
				&kaimo_bridge_fns);
}
