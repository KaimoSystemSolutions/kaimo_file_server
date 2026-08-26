using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_file_change_log_CreatedAtUtc",
                table: "file_change_log",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_file_change_log_ShareId_Seq",
                table: "file_change_log",
                columns: new[] { "ShareId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_change_log");
        }
    }
}
