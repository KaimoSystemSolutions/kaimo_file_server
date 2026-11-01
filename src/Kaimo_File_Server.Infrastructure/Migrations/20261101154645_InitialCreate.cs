using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "backup_repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Backend = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    EncryptedSecrets = table.Column<string>(type: "text", nullable: true),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: true),
                    ResticRepositoryId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    IsAppendOnly = table.Column<bool>(type: "boolean", nullable: false),
                    MinFreeSpaceGb = table.Column<int>(type: "integer", nullable: false),
                    MaintenanceSchedule = table.Column<string>(type: "text", nullable: false),
                    LastMaintenanceSlotUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadDataMonthly = table.Column<bool>(type: "boolean", nullable: false),
                    TestRestoreSampleSize = table.Column<int>(type: "integer", nullable: false),
                    MirrorTargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastReadDataAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    KitDownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    KitConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    KitConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastReachableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_repositories_backup_repositories_MirrorTargetId",
                        column: x => x.MirrorTargetId,
                        principalTable: "backup_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "client_activity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HourUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApiRequests = table.Column<long>(type: "bigint", nullable: false),
                    WebDavRequests = table.Column<long>(type: "bigint", nullable: false),
                    RejectedRequests = table.Column<long>(type: "bigint", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_activity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "config_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "department_users",
                columns: table => new
                {
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_users", x => new { x.DepartmentId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ParentDepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    default_file_permission = table.Column<long>(type: "bigint", nullable: true),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "file_change_log",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    OldPath = table.Column<string>(type: "text", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_change_log", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "file_metadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAccessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_metadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "file_version_missing_paths",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MissingSinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_version_missing_paths", x => new { x.ShareId, x.FilePath });
                });

            migrationBuilder.CreateTable(
                name: "file_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SnapshotTimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000001"))
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "login_attempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepeatCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSeq = table.Column<long>(type: "bigint", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RecipientsJson = table.Column<string>(type: "text", nullable: false),
                    ThrottleMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SubjectTemplate = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HtmlTemplate = table.Column<string>(type: "text", nullable: false),
                    TextTemplate = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_events",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectUserIdsJson = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_events", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "provider_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizationMode = table.Column<int>(type: "integer", nullable: false),
                    TenantOrOrganizationId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PublicClientId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SecretReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AllowedRedirectBaseUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AllowedScopes = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "revoked_web_tokens",
                columns: table => new
                {
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revoked_web_tokens", x => x.Jti);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ManagementPermissions = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    IsSystemRole = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "samba_lifecycle_event_receipts",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenameVersionsCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_samba_lifecycle_event_receipts", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "scoped_role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeType = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scoped_role_assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "search_documents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ShareName = table.Column<string>(type: "text", nullable: false),
                    AbsolutePath = table.Column<string>(type: "text", nullable: false),
                    SharePath = table.Column<string>(type: "text", nullable: false),
                    FileType = table.Column<string>(type: "text", nullable: false),
                    IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentTsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, defaultValueSql: "''::tsvector"),
                    FileModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IndexedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "search_terms",
                columns: table => new
                {
                    Term = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_terms", x => x.Term);
                });

            migrationBuilder.CreateTable(
                name: "share_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000001")),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsShareHidden = table.Column<bool>(type: "boolean", nullable: false),
                    IsRecycleEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsUserHomes = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RecycleMaxSizeMode = table.Column<int>(type: "integer", nullable: false),
                    RecycleMaxSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    RecycleRetentionDays = table.Column<int>(type: "integer", nullable: true),
                    CloudSettings = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "share_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedToken = table.Column<string>(type: "text", nullable: true),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootRelativePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BaseAddress = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxAccessCount = table.Column<int>(type: "integer", nullable: true),
                    AccessCount = table.Column<int>(type: "integer", nullable: false),
                    MaxBytesPerSecond = table.Column<long>(type: "bigint", nullable: true),
                    MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    MaxTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    AllowedExtensions = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "storage_authorization_transactions",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourcePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InitiatingUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedContext = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_authorization_transactions", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "storage_device_authorization_sessions",
                columns: table => new
                {
                    SessionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    NextPollAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PollLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    PollLeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_device_authorization_sessions", x => x.SessionHash);
                });

            migrationBuilder.CreateTable(
                name: "user_groups",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_groups", x => new { x.UserId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Photo = table.Column<byte[]>(type: "bytea", nullable: true),
                    PhotoContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CanChangePassword = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    NtHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    HomeDirectoryEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ServiceAccess = table.Column<int>(type: "integer", nullable: false, defaultValue: 15),
                    SmbUnlockRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "backup_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ContentLevel = table.Column<int>(type: "integer", nullable: false),
                    IncludeRecycleBin = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeVersions = table.Column<bool>(type: "boolean", nullable: false),
                    Schedule = table.Column<string>(type: "text", nullable: false),
                    Retention = table.Column<string>(type: "text", nullable: false),
                    AlertAfterDays = table.Column<int>(type: "integer", nullable: false),
                    LastScheduledSlotUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_jobs_backup_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "backup_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnapshotIdsJson = table.Column<string>(type: "text", nullable: true),
                    FilesNew = table.Column<long>(type: "bigint", nullable: false),
                    FilesChanged = table.Column<long>(type: "bigint", nullable: false),
                    FilesUnmodified = table.Column<long>(type: "bigint", nullable: false),
                    BytesAdded = table.Column<long>(type: "bigint", nullable: false),
                    BytesProcessed = table.Column<long>(type: "bigint", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LogExcerpt = table.Column<string>(type: "text", nullable: true),
                    DetailsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_runs_backup_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "backup_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_snapshots",
                columns: table => new
                {
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: true),
                    PoolPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ContentLevel = table.Column<int>(type: "integer", nullable: false),
                    PathsJson = table.Column<string>(type: "text", nullable: false),
                    TagsJson = table.Column<string>(type: "text", nullable: false),
                    FileCount = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_snapshots", x => new { x.RepositoryId, x.SnapshotId });
                    table.ForeignKey(
                        name: "FK_backup_snapshots_backup_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "backup_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_repository_departments",
                columns: table => new
                {
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_repository_departments", x => new { x.RepositoryId, x.DepartmentId });
                    table.ForeignKey(
                        name: "FK_backup_repository_departments_backup_repositories_Repositor~",
                        column: x => x.RepositoryId,
                        principalTable: "backup_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_backup_repository_departments_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "access_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileMetadataId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    Permissions = table.Column<long>(type: "bigint", nullable: false),
                    Inheritance = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_entries_file_metadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "file_metadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "storage_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizationMode = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: true),
                    AccountDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ProviderAccountId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProviderTenantId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProviderSubjectId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EffectiveScopes = table.Column<string>(type: "text", nullable: true),
                    EncryptedCredentialPayload = table.Column<string>(type: "text", nullable: true),
                    CredentialFormatVersion = table.Column<int>(type: "integer", nullable: false),
                    ProtectorPurposeVersion = table.Column<int>(type: "integer", nullable: false),
                    CredentialUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulUseAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_storage_connections_provider_profiles_ProviderProfileId",
                        column: x => x.ProviderProfileId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recycle_bin_entries",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    CtimeTicks = table.Column<long>(type: "bigint", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recycle_bin_entries", x => new { x.ShareId, x.Path });
                    table.ForeignKey(
                        name: "FK_recycle_bin_entries_share_definitions_ShareId",
                        column: x => x.ShareId,
                        principalTable: "share_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayNameSetByAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    Platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HardwareIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PushToken = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_devices_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recovery_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_recovery_codes_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_second_factors",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretProtected = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedStep = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_second_factors", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_user_second_factors_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_job_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: true),
                    PoolPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_job_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backup_job_sources_backup_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "backup_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_backup_job_sources_share_definitions_ShareId",
                        column: x => x.ShareId,
                        principalTable: "share_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cloud_access_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RemoteRootPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RemoteRootItemId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_access_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cloud_access_shares_storage_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "storage_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "storage_connection_credential_leases",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_connection_credential_leases", x => x.ConnectionId);
                    table.ForeignKey(
                        name: "FK_storage_connection_credential_leases_storage_connections_Co~",
                        column: x => x.ConnectionId,
                        principalTable: "storage_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RemotePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RemoteProviderItemId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Schedule = table.Column<string>(type: "text", nullable: false),
                    AdvancedSettings = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RequiresRemoteFolderSelection = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RunAsUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MigrationSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MigrationSourceChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_definitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_definitions_share_definitions_LocalShareId",
                        column: x => x.LocalShareId,
                        principalTable: "share_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sync_definitions_storage_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "storage_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sync_definitions_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sync_definitions_users_RunAsUserId",
                        column: x => x.RunAsUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_request_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_request_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_client_request_receipts_sync_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "sync_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_sync_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, defaultValue: ""),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_sync_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_sync_profiles_sync_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "sync_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_sync_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "sync_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cloud_access_grants",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<int>(type: "integer", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_access_grants", x => new { x.ShareId, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_cloud_access_grants_cloud_access_shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "cloud_access_shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_definition_runtimes",
                columns: table => new
                {
                    SyncDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastRunFailureCount = table.Column<int>(type: "integer", nullable: true),
                    LastRunFailures = table.Column<string>(type: "text", nullable: true),
                    LastSyncManifest = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_definition_runtimes", x => x.SyncDefinitionId);
                    table.ForeignKey(
                        name: "FK_sync_definition_runtimes_sync_definitions_SyncDefinitionId",
                        column: x => x.SyncDefinitionId,
                        principalTable: "sync_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "provider_profiles",
                columns: new[] { "Id", "AllowedRedirectBaseUri", "AllowedScopes", "AuthorizationMode", "CreatedAtUtc", "Enabled", "Name", "ProviderId", "PublicClientId", "SecretReference", "TenantOrOrganizationId", "UpdatedAtUtc" },
                values: new object[] { new Guid("e76c2a65-8bbd-4fac-b3f5-f663bc9df5df"), null, "offline_access Files.ReadWrite User.Read", 0, new DateTime(2026, 8, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Kaimo Microsoft public client", "onedrive", "e966f5be-e8a1-4c67-b322-aac34c1ab642", null, "common", new DateTime(2026, 8, 18, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_access_entries_FileMetadataId",
                table: "access_entries",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_job_sources_JobId",
                table: "backup_job_sources",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_job_sources_ShareId",
                table: "backup_job_sources",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_Name",
                table: "backup_jobs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_RepositoryId",
                table: "backup_jobs",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_repositories_MirrorTargetId",
                table: "backup_repositories",
                column: "MirrorTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_repositories_Name",
                table: "backup_repositories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_repository_departments_DepartmentId",
                table: "backup_repository_departments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_JobId_QueuedAtUtc",
                table: "backup_runs",
                columns: new[] { "JobId", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_QueuedAtUtc",
                table: "backup_runs",
                column: "QueuedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_RepositoryId",
                table: "backup_runs",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_Status",
                table: "backup_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_backup_snapshots_JobId",
                table: "backup_snapshots",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_snapshots_ShareId_TimeUtc",
                table: "backup_snapshots",
                columns: new[] { "ShareId", "TimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_client_activity_Address_HourUtc",
                table: "client_activity",
                columns: new[] { "Address", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_client_activity_LastSeenUtc",
                table: "client_activity",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_client_request_receipts_CreatedAtUtc",
                table: "client_request_receipts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_client_request_receipts_DeviceId_IdempotencyKey",
                table: "client_request_receipts",
                columns: new[] { "DeviceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_grants_PrincipalId",
                table: "cloud_access_grants",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_ConnectionId",
                table: "cloud_access_shares",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_DepartmentId",
                table: "cloud_access_shares",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_Name",
                table: "cloud_access_shares",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_department_users_UserId",
                table: "department_users",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_departments_Name",
                table: "departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_ParentDepartmentId",
                table: "departments",
                column: "ParentDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_device_sync_profiles_DeviceId",
                table: "device_sync_profiles",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_device_sync_profiles_DeviceId_ShareId_RelativePath",
                table: "device_sync_profiles",
                columns: new[] { "DeviceId", "ShareId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_sync_profiles_ShareId",
                table: "device_sync_profiles",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_device_sync_profiles_UserId",
                table: "device_sync_profiles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_file_change_log_CreatedAtUtc",
                table: "file_change_log",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_file_change_log_ShareId_Seq",
                table: "file_change_log",
                columns: new[] { "ShareId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_file_metadata_ShareId_Path",
                table: "file_metadata",
                columns: new[] { "ShareId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_ShareId_FilePath_ContentHash",
                table: "file_versions",
                columns: new[] { "ShareId", "FilePath", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_ShareId_FilePath_SnapshotTimestampUtc",
                table: "file_versions",
                columns: new[] { "ShareId", "FilePath", "SnapshotTimestampUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_SnapshotTimestampUtc",
                table: "file_versions",
                column: "SnapshotTimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_StoragePath",
                table: "file_versions",
                column: "StoragePath");

            migrationBuilder.CreateIndex(
                name: "IX_groups_DepartmentId",
                table: "groups",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_AtUtc",
                table: "login_attempts",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_LockedUntilUtc",
                table: "login_attempts",
                column: "LockedUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_Username_AtUtc",
                table: "login_attempts",
                columns: new[] { "Username", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_deliveries_CreatedAtUtc",
                table: "mail_deliveries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_mail_deliveries_RuleId_DedupKey_ToAddress_CreatedAtUtc",
                table: "mail_deliveries",
                columns: new[] { "RuleId", "DedupKey", "ToAddress", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_deliveries_Status_NextAttemptUtc",
                table: "mail_deliveries",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_rules_EventType",
                table: "mail_rules",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_mail_templates_EventType_Language",
                table: "mail_templates",
                columns: new[] { "EventType", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_Status_Seq",
                table: "notification_events",
                columns: new[] { "Status", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_Name",
                table: "provider_profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_ProviderId_Enabled",
                table: "provider_profiles",
                columns: new[] { "ProviderId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_entries_ShareId_FirstSeenUtc",
                table: "recycle_bin_entries",
                columns: new[] { "ShareId", "FirstSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_DeviceId",
                table: "refresh_tokens",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_revoked_web_tokens_ExpiresAtUtc",
                table: "revoked_web_tokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_samba_lifecycle_event_receipts_CompletedAtUtc",
                table: "samba_lifecycle_event_receipts",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_samba_lifecycle_event_receipts_LeaseUntilUtc",
                table: "samba_lifecycle_event_receipts",
                column: "LeaseUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_scoped_role_assignments_PrincipalId",
                table: "scoped_role_assignments",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_scoped_role_assignments_PrincipalId_RoleId_ScopeType_ScopeId",
                table: "scoped_role_assignments",
                columns: new[] { "PrincipalId", "RoleId", "ScopeType", "ScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scoped_role_assignments_RoleId",
                table: "scoped_role_assignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_scoped_role_assignments_ScopeType_ScopeId",
                table: "scoped_role_assignments",
                columns: new[] { "ScopeType", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_AbsolutePath",
                table: "search_documents",
                column: "AbsolutePath")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_ContentTsv",
                table: "search_documents",
                column: "ContentTsv")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_FileName",
                table: "search_documents",
                column: "FileName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_ShareName_SharePath",
                table: "search_documents",
                columns: new[] { "ShareName", "SharePath" })
                .Annotation("Npgsql:IndexOperators", new[] { "text_ops", "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_search_terms_Term",
                table: "search_terms",
                column: "Term")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_share_definitions_DepartmentId",
                table: "share_definitions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_share_definitions_IsUserHomes",
                table: "share_definitions",
                column: "IsUserHomes",
                unique: true,
                filter: "\"IsUserHomes\"");

            migrationBuilder.CreateIndex(
                name: "IX_share_definitions_Name",
                table: "share_definitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_links_ShareId",
                table: "share_links",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_share_links_Token",
                table: "share_links",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_links_TokenHash",
                table: "share_links",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_storage_authorization_transactions_ExpiresAtUtc",
                table: "storage_authorization_transactions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_storage_authorization_transactions_ResourceId_ProviderId",
                table: "storage_authorization_transactions",
                columns: new[] { "ResourceId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_connection_credential_leases_ExpiresAtUtc",
                table: "storage_connection_credential_leases",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_storage_connections_Name",
                table: "storage_connections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_storage_connections_ProviderProfileId",
                table: "storage_connections",
                column: "ProviderProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_device_authorization_sessions_ExpiresAtUtc",
                table: "storage_device_authorization_sessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_storage_device_authorization_sessions_PollLeaseUntilUtc",
                table: "storage_device_authorization_sessions",
                column: "PollLeaseUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_sync_definition_runtimes_LastSuccessfulRunAtUtc",
                table: "sync_definition_runtimes",
                column: "LastSuccessfulRunAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_sync_definitions_ConnectionId",
                table: "sync_definitions",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_definitions_CreatedByUserId",
                table: "sync_definitions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_definitions_Enabled_RunAsUserId",
                table: "sync_definitions",
                columns: new[] { "Enabled", "RunAsUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_definitions_LocalShareId_LocalPath",
                table: "sync_definitions",
                columns: new[] { "LocalShareId", "LocalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_definitions_RunAsUserId",
                table: "sync_definitions",
                column: "RunAsUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_devices_UserId",
                table: "sync_devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_devices_UserId_HardwareIdHash",
                table: "sync_devices",
                columns: new[] { "UserId", "HardwareIdHash" });

            migrationBuilder.CreateIndex(
                name: "IX_user_recovery_codes_UserId_CodeHash",
                table: "user_recovery_codes",
                columns: new[] { "UserId", "CodeHash" });

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_entries");

            migrationBuilder.DropTable(
                name: "backup_job_sources");

            migrationBuilder.DropTable(
                name: "backup_repository_departments");

            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "backup_snapshots");

            migrationBuilder.DropTable(
                name: "client_activity");

            migrationBuilder.DropTable(
                name: "client_request_receipts");

            migrationBuilder.DropTable(
                name: "cloud_access_grants");

            migrationBuilder.DropTable(
                name: "config_settings");

            migrationBuilder.DropTable(
                name: "department_users");

            migrationBuilder.DropTable(
                name: "device_sync_profiles");

            migrationBuilder.DropTable(
                name: "file_change_log");

            migrationBuilder.DropTable(
                name: "file_version_missing_paths");

            migrationBuilder.DropTable(
                name: "file_versions");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "login_attempts");

            migrationBuilder.DropTable(
                name: "mail_deliveries");

            migrationBuilder.DropTable(
                name: "mail_rules");

            migrationBuilder.DropTable(
                name: "mail_templates");

            migrationBuilder.DropTable(
                name: "notification_events");

            migrationBuilder.DropTable(
                name: "recycle_bin_entries");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "revoked_web_tokens");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "samba_lifecycle_event_receipts");

            migrationBuilder.DropTable(
                name: "scoped_role_assignments");

            migrationBuilder.DropTable(
                name: "search_documents");

            migrationBuilder.DropTable(
                name: "search_terms");

            migrationBuilder.DropTable(
                name: "share_links");

            migrationBuilder.DropTable(
                name: "storage_authorization_transactions");

            migrationBuilder.DropTable(
                name: "storage_connection_credential_leases");

            migrationBuilder.DropTable(
                name: "storage_device_authorization_sessions");

            migrationBuilder.DropTable(
                name: "sync_definition_runtimes");

            migrationBuilder.DropTable(
                name: "user_groups");

            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropTable(
                name: "user_second_factors");

            migrationBuilder.DropTable(
                name: "file_metadata");

            migrationBuilder.DropTable(
                name: "backup_jobs");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "cloud_access_shares");

            migrationBuilder.DropTable(
                name: "sync_devices");

            migrationBuilder.DropTable(
                name: "sync_definitions");

            migrationBuilder.DropTable(
                name: "backup_repositories");

            migrationBuilder.DropTable(
                name: "share_definitions");

            migrationBuilder.DropTable(
                name: "storage_connections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "provider_profiles");
        }
    }
}
