using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempts", x => x.Id);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_activity");

            migrationBuilder.DropTable(
                name: "login_attempts");
        }
    }
}
