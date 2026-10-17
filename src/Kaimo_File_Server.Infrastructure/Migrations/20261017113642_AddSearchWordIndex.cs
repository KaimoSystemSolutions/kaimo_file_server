using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchWordIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_search_documents_Content",
                table: "search_documents");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "ContentTsv",
                table: "search_documents",
                type: "tsvector",
                nullable: false,
                defaultValueSql: "''::tsvector");

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

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_ContentTsv",
                table: "search_documents",
                column: "ContentTsv")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_search_terms_Term",
                table: "search_terms",
                column: "Term")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_terms");

            migrationBuilder.DropIndex(
                name: "IX_search_documents_ContentTsv",
                table: "search_documents");

            migrationBuilder.DropColumn(
                name: "ContentTsv",
                table: "search_documents");

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_Content",
                table: "search_documents",
                column: "Content")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }
    }
}
