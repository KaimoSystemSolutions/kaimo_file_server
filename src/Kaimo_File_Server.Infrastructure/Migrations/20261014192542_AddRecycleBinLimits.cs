using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecycleBinLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RecycleMaxSizeBytes",
                table: "share_definitions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecycleMaxSizeMode",
                table: "share_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecycleRetentionDays",
                table: "share_definitions",
                type: "integer",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_recycle_bin_entries_ShareId_FirstSeenUtc",
                table: "recycle_bin_entries",
                columns: new[] { "ShareId", "FirstSeenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recycle_bin_entries");

            migrationBuilder.DropColumn(
                name: "RecycleMaxSizeBytes",
                table: "share_definitions");

            migrationBuilder.DropColumn(
                name: "RecycleMaxSizeMode",
                table: "share_definitions");

            migrationBuilder.DropColumn(
                name: "RecycleRetentionDays",
                table: "share_definitions");
        }
    }
}
