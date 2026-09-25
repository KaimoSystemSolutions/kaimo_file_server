using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SecurityHardeningRound3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Every existing account gets its own random stamp. Tokens issued before this
            // migration carry no stamp and are rejected, so every user signs in once again.
            migrationBuilder.Sql(
                """UPDATE users SET "SecurityStamp" = replace(gen_random_uuid()::text, '-', '');""");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "share_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedToken",
                table: "share_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "share_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing links become resolvable by hash right away; the web host's startup
            // backfill then replaces their plain-text "Token" by the encrypted copy.
            migrationBuilder.Sql(
                """
                UPDATE share_links
                SET "TokenHash" = encode(sha256(convert_to("Token", 'UTF8')), 'hex')
                WHERE "Token" IS NOT NULL;
                """);

            // WebDAV targets over plain http:// now need an explicit opt-in. Existing ones
            // were configured that way deliberately, so they get the opt-in and keep working.
            // Unparsable settings are left unchanged (such a connection already fails validation).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE r record;
                BEGIN
                    FOR r IN SELECT "Id", "SettingsJson" FROM storage_connections
                             WHERE lower("ProviderId") = 'webdav' AND "SettingsJson" ILIKE '%http://%'
                    LOOP
                        BEGIN
                            IF (r."SettingsJson"::jsonb ->> 'ServerUrl') ILIKE 'http://%' THEN
                                UPDATE storage_connections
                                SET "SettingsJson" = jsonb_set(r."SettingsJson"::jsonb, '{AllowInsecureHttp}', 'true'::jsonb)::text
                                WHERE "Id" = r."Id";
                            END IF;
                        EXCEPTION WHEN others THEN
                            NULL;
                        END;
                    END LOOP;
                END $$;
                """);

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

            migrationBuilder.CreateIndex(
                name: "IX_share_links_TokenHash",
                table: "share_links",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revoked_web_tokens_ExpiresAtUtc",
                table: "revoked_web_tokens",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "revoked_web_tokens");

            migrationBuilder.DropIndex(
                name: "IX_share_links_TokenHash",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ProtectedToken",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "share_links");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "share_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
