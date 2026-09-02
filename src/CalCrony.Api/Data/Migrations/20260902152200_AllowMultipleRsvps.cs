using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Lets a member hold an RSVP on more than one option of the same event (RSVP v2
    /// §3.3): an opt-in flag on events and, as a template field, on series, and the RSVP unique
    /// index widened from (EventId, UserId) to (EventId, UserId, OptionId). Up is lossless — no
    /// existing row violates the wider index. Down is NOT: the old index cannot hold two rows for
    /// one member, so every member keeps only their earliest RSVP on each event before it is
    /// restored. Rolling the IMAGE back without running Down is safe — the previous code ignores
    /// the columns and never inserts a second row.</summary>
    /// <inheritdoc />
    public partial class AllowMultipleRsvps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rsvps_EventId_UserId",
                table: "Rsvps");

            migrationBuilder.AddColumn<bool>(
                name: "AllowMultipleRsvps",
                table: "EventSeries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMultipleRsvps",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Rsvps_EventId_UserId_OptionId",
                table: "Rsvps",
                columns: new[] { "EventId", "UserId", "OptionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Collapse each member back to one RSVP per event — the earliest, by the same
            // CreatedAt order the waitlist queues on (Id breaks ties) — BEFORE the old unique
            // index is restored, or its creation would fail on the very rows it forbids.
            migrationBuilder.Sql("""
                DELETE FROM "Rsvps"
                WHERE "Id" IN (
                    SELECT "Id"
                    FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (PARTITION BY "EventId", "UserId" ORDER BY "CreatedAt", "Id") AS seat
                        FROM "Rsvps"
                    ) AS ranked
                    WHERE ranked.seat > 1
                );
                """);

            migrationBuilder.DropIndex(
                name: "IX_Rsvps_EventId_UserId_OptionId",
                table: "Rsvps");

            migrationBuilder.DropColumn(
                name: "AllowMultipleRsvps",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "AllowMultipleRsvps",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Rsvps_EventId_UserId",
                table: "Rsvps",
                columns: new[] { "EventId", "UserId" },
                unique: true);
        }
    }
}
