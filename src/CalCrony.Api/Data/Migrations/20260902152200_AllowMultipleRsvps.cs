using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Lets a member hold an RSVP on more than one option of the same event (RSVP v2
    /// §3.3): an opt-in flag on events and, as a template field, on series, and the RSVP unique
    /// index widened from (EventId, UserId) to (EventId, UserId, OptionId). Up is lossless — no
    /// existing row violates the wider index. Down is NOT: the old index cannot hold two rows for
    /// one member, so every member keeps only their earliest RSVP on each event before it is
    /// restored, and a revoke is enqueued for each role a discarded seat carried that the kept seat
    /// does not — the downgraded app cannot discover those roles later.
    /// Rolling the IMAGE back without running Down is safe only while no member holds more than
    /// one row: the previous PutRsvp moves a member's first row onto the clicked option, which
    /// collides with their other row when they already hold it. Once members hold several seats,
    /// run Down first.</summary>
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
            // Each member's rows on an event ranked by the same CreatedAt order the waitlist queues
            // on (Id breaks ties): seat 1 is kept, the rest are discarded below. A discarded SEATED
            // row on a role-bearing option of a live event loses that role for the member unless
            // the kept row (when itself seated) carries the same role — one RevokeAttendeeRole
            // (type 11) per (event, member, role), the way the app's own end/delete sweep would
            // enqueue it, since the downgraded code can no longer see those seats.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", "EventId", "UserId", "OptionId", "Waitlisted",
                           ROW_NUMBER() OVER (PARTITION BY "EventId", "UserId" ORDER BY "CreatedAt", "Id") AS seat
                    FROM "Rsvps"
                )
                INSERT INTO "Deliveries" ("Id", "Type", "ChannelId", "PayloadJson", "DueAt", "Status", "Attempts", "CreatedAt")
                SELECT gen_random_uuid(),
                       11,
                       e."ChannelId",
                       json_build_object('EventId', e."Id", 'GuildId', e."GuildId", 'RoleId', o."AttendeeRoleId", 'UserId', r."UserId")::text,
                       now(),
                       0,
                       0,
                       now()
                FROM ranked AS r
                JOIN "RsvpOptions" AS o ON o."Id" = r."OptionId"
                JOIN "Events" AS e ON e."Id" = r."EventId"
                WHERE r.seat > 1
                  AND NOT r."Waitlisted"
                  AND o."AttendeeRoleId" IS NOT NULL
                  AND e."Status" IN (0, 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ranked AS k
                      JOIN "RsvpOptions" AS ko ON ko."Id" = k."OptionId"
                      WHERE k."EventId" = r."EventId"
                        AND k."UserId" = r."UserId"
                        AND k.seat = 1
                        AND NOT k."Waitlisted"
                        AND ko."AttendeeRoleId" = o."AttendeeRoleId")
                GROUP BY e."Id", e."GuildId", e."ChannelId", r."UserId", o."AttendeeRoleId";
                """);

            // Collapse each member back to one RSVP per event BEFORE the old unique index is
            // restored, or its creation would fail on the very rows it forbids.
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
