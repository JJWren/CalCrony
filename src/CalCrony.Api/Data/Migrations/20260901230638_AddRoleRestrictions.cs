using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>RSVP v2 §3.5 role-restricted signup (ADR 0004). Additive only: a per-option and a
    /// per-poll <c>AllowedRoleIds</c> array defaulting to empty (unrestricted — no existing row
    /// changes behaviour), the guild's role-sync marker, and the two bot-written snapshot tables
    /// the web's role check reads. No backfill: restrictions are opt-in and configured in Discord
    /// after this ships, and snapshots are pushed by the bot on demand.</summary>
    /// <inheritdoc />
    public partial class AddRoleRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long[]>(
                name: "AllowedRoleIds",
                table: "RsvpOptions",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);

            migrationBuilder.AddColumn<long[]>(
                name: "AllowedRoleIds",
                table: "Polls",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);

            migrationBuilder.AddColumn<Instant>(
                name: "RolesSyncedAt",
                table: "Guilds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuildMemberRoles",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RoleIds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    SnapshotAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMemberRoles", x => new { x.GuildId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "GuildRoles",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SnapshotAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildRoles", x => new { x.GuildId, x.RoleId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildMemberRoles");

            migrationBuilder.DropTable(
                name: "GuildRoles");

            migrationBuilder.DropColumn(
                name: "AllowedRoleIds",
                table: "RsvpOptions");

            migrationBuilder.DropColumn(
                name: "AllowedRoleIds",
                table: "Polls");

            migrationBuilder.DropColumn(
                name: "RolesSyncedAt",
                table: "Guilds");
        }
    }
}
