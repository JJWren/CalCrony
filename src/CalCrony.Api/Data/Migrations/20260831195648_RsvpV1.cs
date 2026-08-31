using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RsvpV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Waitlisted",
                table: "Rsvps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAttending",
                table: "RsvpOptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RsvpCloseMinutesBefore",
                table: "EventSeries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RsvpOptionsJson",
                table: "EventSeries",
                type: "character varying(10240)",
                maxLength: 10240,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RsvpCloseMinutesBefore",
                table: "Events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RsvpCloseSynced",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Instant>(
                name: "RsvpClosesAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill the attending flag onto every existing event's first option (lowest
            // SortOrder, id as tie-break) — pre-RSVP-v1 events all start with the default set,
            // where "Going" is SortOrder 0, so this preserves their exact prior semantics.
            migrationBuilder.Sql("""
                UPDATE "RsvpOptions" AS o SET "IsAttending" = TRUE
                FROM (
                    SELECT DISTINCT ON ("EventId") "Id"
                    FROM "RsvpOptions"
                    ORDER BY "EventId", "SortOrder", "Id"
                ) AS first
                WHERE o."Id" = first."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Waitlisted",
                table: "Rsvps");

            migrationBuilder.DropColumn(
                name: "IsAttending",
                table: "RsvpOptions");

            migrationBuilder.DropColumn(
                name: "RsvpCloseMinutesBefore",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "RsvpOptionsJson",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "RsvpCloseMinutesBefore",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RsvpCloseSynced",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RsvpClosesAt",
                table: "Events");
        }
    }
}
