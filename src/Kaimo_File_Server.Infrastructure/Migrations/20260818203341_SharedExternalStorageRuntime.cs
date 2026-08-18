using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SharedExternalStorageRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_authorization_transactions", x => x.TokenHash);
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
                name: "IX_storage_device_authorization_sessions_ExpiresAtUtc",
                table: "storage_device_authorization_sessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_storage_device_authorization_sessions_PollLeaseUntilUtc",
                table: "storage_device_authorization_sessions",
                column: "PollLeaseUntilUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_authorization_transactions");

            migrationBuilder.DropTable(
                name: "storage_connection_credential_leases");

            migrationBuilder.DropTable(
                name: "storage_device_authorization_sessions");
        }
    }
}
