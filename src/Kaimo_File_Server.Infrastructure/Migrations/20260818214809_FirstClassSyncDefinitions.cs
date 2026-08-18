using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FirstClassSyncDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_definition_runtimes");

            migrationBuilder.DropTable(
                name: "sync_definitions");
        }
    }
}
