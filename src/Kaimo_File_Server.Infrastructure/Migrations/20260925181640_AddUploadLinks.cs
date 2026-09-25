using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedExtensions",
                table: "share_links",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "share_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MaxFileSizeBytes",
                table: "share_links",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MaxTotalBytes",
                table: "share_links",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UploadedBytes",
                table: "share_links",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedExtensions",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "MaxFileSizeBytes",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "MaxTotalBytes",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "UploadedBytes",
                table: "share_links");
        }
    }
}
