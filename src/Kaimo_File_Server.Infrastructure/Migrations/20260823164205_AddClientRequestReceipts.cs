using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientRequestReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_client_request_receipts_CreatedAtUtc",
                table: "client_request_receipts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_client_request_receipts_DeviceId_IdempotencyKey",
                table: "client_request_receipts",
                columns: new[] { "DeviceId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_request_receipts");
        }
    }
}
