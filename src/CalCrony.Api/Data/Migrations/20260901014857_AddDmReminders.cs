using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDmReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DmReminders",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Instant>(
                name: "DmRemindersBlockedAt",
                table: "UserProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "DmRemindersEnabledAt",
                table: "UserProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DmRemindersOffered",
                table: "UserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Instant>(
                name: "ClaimedAt",
                table: "Deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecipientUserId",
                table: "Deliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_RecipientUserId",
                table: "Deliveries",
                column: "RecipientUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deliveries_RecipientUserId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "DmReminders",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DmRemindersBlockedAt",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DmRemindersEnabledAt",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DmRemindersOffered",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "RecipientUserId",
                table: "Deliveries");
        }
    }
}
