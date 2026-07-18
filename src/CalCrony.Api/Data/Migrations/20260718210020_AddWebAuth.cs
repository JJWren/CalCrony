using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWebAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarHash",
                table: "UserProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "UserProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserGuildMemberships",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    GuildName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IconHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CanManage = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGuildMemberships", x => new { x.UserId, x.GuildId });
                });

            migrationBuilder.CreateTable(
                name: "WebLoginStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReturnUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebLoginStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebRefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebLoginStates_Token",
                table: "WebLoginStates",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebRefreshTokens_TokenHash",
                table: "WebRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebRefreshTokens_UserId",
                table: "WebRefreshTokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserGuildMemberships");

            migrationBuilder.DropTable(
                name: "WebLoginStates");

            migrationBuilder.DropTable(
                name: "WebRefreshTokens");

            migrationBuilder.DropColumn(
                name: "AvatarHash",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "UserProfiles");
        }
    }
}
