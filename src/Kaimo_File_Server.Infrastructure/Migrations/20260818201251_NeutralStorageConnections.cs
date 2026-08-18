using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <summary>
    /// Renames the existing Cloud Access connection table and adds the neutral
    /// connection metadata without recreating or decrypting credential records.
    /// </summary>
    public partial class NeutralStorageConnections : Migration
    {
        private static readonly Guid MicrosoftPublicProfileId =
            Guid.Parse("e76c2a65-8bbd-4fac-b3f5-f663bc9df5df");

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cloud_access_shares_cloud_access_connections_ConnectionId",
                table: "cloud_access_shares");

            migrationBuilder.RenameTable(
                name: "cloud_access_connections",
                newName: "storage_connections");

            migrationBuilder.RenameColumn(
                name: "Provider",
                table: "storage_connections",
                newName: "ProviderId");

            migrationBuilder.RenameColumn(
                name: "ProtectedCredentials",
                table: "storage_connections",
                newName: "EncryptedCredentialPayload");

            migrationBuilder.RenameColumn(
                name: "LastError",
                table: "storage_connections",
                newName: "LastErrorCode");

            migrationBuilder.RenameIndex(
                name: "IX_cloud_access_connections_Name",
                table: "storage_connections",
                newName: "IX_storage_connections_Name");

            migrationBuilder.RenameIndex(
                name: "IX_cloud_access_connections_DepartmentId",
                table: "storage_connections",
                newName: "IX_storage_connections_DepartmentId");

            migrationBuilder.Sql(
                "ALTER TABLE storage_connections RENAME CONSTRAINT \"PK_cloud_access_connections\" TO \"PK_storage_connections\";");

            migrationBuilder.CreateTable(
                name: "provider_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizationMode = table.Column<int>(type: "integer", nullable: false),
                    TenantOrOrganizationId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PublicClientId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SecretReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AllowedRedirectBaseUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AllowedScopes = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_profiles", x => x.Id);
                });

            migrationBuilder.AddColumn<int>(
                name: "AuthorizationMode",
                table: "storage_connections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ConcurrencyVersion",
                table: "storage_connections",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<int>(
                name: "CredentialFormatVersion",
                table: "storage_connections",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialUpdatedAtUtc",
                table: "storage_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EffectiveScopes",
                table: "storage_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessfulUseAtUtc",
                table: "storage_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProtectorPurposeVersion",
                table: "storage_connections",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "ProviderAccountId",
                table: "storage_connections",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderProfileId",
                table: "storage_connections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubjectId",
                table: "storage_connections",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderTenantId",
                table: "storage_connections",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "storage_connections",
                type: "text",
                nullable: true);

            var profileTimestamp = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "provider_profiles",
                columns:
                [
                    "Id", "ProviderId", "Name", "AuthorizationMode",
                    "TenantOrOrganizationId", "PublicClientId", "SecretReference",
                    "AllowedRedirectBaseUri", "AllowedScopes", "Enabled",
                    "CreatedAtUtc", "UpdatedAtUtc"
                ],
                values:
                [
                    MicrosoftPublicProfileId, "onedrive", "Kaimo Microsoft public client", 0,
                    "common", "e966f5be-e8a1-4c67-b322-aac34c1ab642", null,
                    null, "offline_access Files.ReadWrite User.Read", true,
                    profileTimestamp, profileTimestamp
                ]);

            migrationBuilder.Sql($"""
                UPDATE storage_connections
                SET "ProviderProfileId" = '{MicrosoftPublicProfileId:D}'
                WHERE lower("ProviderId") = 'onedrive';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_Name",
                table: "provider_profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_ProviderId_Enabled",
                table: "provider_profiles",
                columns: ["ProviderId", "Enabled"]);

            migrationBuilder.CreateIndex(
                name: "IX_storage_connections_ProviderProfileId",
                table: "storage_connections",
                column: "ProviderProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_storage_connections_provider_profiles_ProviderProfileId",
                table: "storage_connections",
                column: "ProviderProfileId",
                principalTable: "provider_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_cloud_access_shares_storage_connections_ConnectionId",
                table: "cloud_access_shares",
                column: "ConnectionId",
                principalTable: "storage_connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cloud_access_shares_storage_connections_ConnectionId",
                table: "cloud_access_shares");

            migrationBuilder.DropForeignKey(
                name: "FK_storage_connections_provider_profiles_ProviderProfileId",
                table: "storage_connections");

            migrationBuilder.DropIndex(
                name: "IX_storage_connections_ProviderProfileId",
                table: "storage_connections");

            migrationBuilder.DropColumn(name: "AuthorizationMode", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ConcurrencyVersion", table: "storage_connections");
            migrationBuilder.DropColumn(name: "CredentialFormatVersion", table: "storage_connections");
            migrationBuilder.DropColumn(name: "CredentialUpdatedAtUtc", table: "storage_connections");
            migrationBuilder.DropColumn(name: "EffectiveScopes", table: "storage_connections");
            migrationBuilder.DropColumn(name: "LastSuccessfulUseAtUtc", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ProtectorPurposeVersion", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ProviderAccountId", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ProviderProfileId", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ProviderSubjectId", table: "storage_connections");
            migrationBuilder.DropColumn(name: "ProviderTenantId", table: "storage_connections");
            migrationBuilder.DropColumn(name: "SettingsJson", table: "storage_connections");

            migrationBuilder.DropTable(name: "provider_profiles");

            migrationBuilder.Sql(
                "ALTER TABLE storage_connections RENAME CONSTRAINT \"PK_storage_connections\" TO \"PK_cloud_access_connections\";");

            migrationBuilder.RenameIndex(
                name: "IX_storage_connections_Name",
                table: "storage_connections",
                newName: "IX_cloud_access_connections_Name");

            migrationBuilder.RenameIndex(
                name: "IX_storage_connections_DepartmentId",
                table: "storage_connections",
                newName: "IX_cloud_access_connections_DepartmentId");

            migrationBuilder.RenameColumn(
                name: "ProviderId",
                table: "storage_connections",
                newName: "Provider");

            migrationBuilder.RenameColumn(
                name: "EncryptedCredentialPayload",
                table: "storage_connections",
                newName: "ProtectedCredentials");

            migrationBuilder.RenameColumn(
                name: "LastErrorCode",
                table: "storage_connections",
                newName: "LastError");

            migrationBuilder.RenameTable(
                name: "storage_connections",
                newName: "cloud_access_connections");

            migrationBuilder.AddForeignKey(
                name: "FK_cloud_access_shares_cloud_access_connections_ConnectionId",
                table: "cloud_access_shares",
                column: "ConnectionId",
                principalTable: "cloud_access_connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
