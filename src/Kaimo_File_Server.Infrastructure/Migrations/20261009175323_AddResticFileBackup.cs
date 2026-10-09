using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResticFileBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    Schedule = table.Column<string>(type: "text", nullable: false),
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_job_sources");

            migrationBuilder.DropTable(
                name: "backup_repository_departments");

            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "backup_snapshots");

            migrationBuilder.DropTable(
                name: "backup_jobs");

            migrationBuilder.DropTable(
                name: "backup_repositories");
        }
    }
}
