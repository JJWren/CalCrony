using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Lets a member hold an RSVP on more than one option of the same event (RSVP v2
    /// §3.3): an opt-in flag on events and, as a template field, on series, and the RSVP unique
    /// index widened from (EventId, UserId) to (EventId, UserId, OptionId). Up is lossless — no
    /// existing row violates the wider index. Down is NOT: the old index cannot hold two rows for
    /// one member, so on each event a member keeps only one row — their seated ATTENDING row when
    /// they hold one (so no attending seat is freed behind a waitlist the downgraded app would not
    /// promote), else their earliest — and a revoke is enqueued for each role a discarded seat
    /// carried that the kept seat does not, since the downgraded app cannot discover those roles
    /// later. Rolling the IMAGE back without running Down is safe only while no member holds more
    /// than one row: the previous PutRsvp moves a member's first row onto the clicked option,
    /// which collides with their other row when they already hold it. Once members hold several
    /// seats, run Down first.</summary>
    /// <inheritdoc />
    public partial class AllowMultipleRsvps : Migration
    {
        /// <summary>Each member's rows on an event ranked for Down: seat 1 survives, the rest are
        /// discarded. A seated row on the event's attending option (the flagged one, else the
        /// lowest SortOrder — the RsvpPolicy.AttendingOption rule) ranks first, so collapsing a
        /// member never frees an attending seat; otherwise the same CreatedAt order the waitlist
        /// queues on decides, with Id breaking ties.</summary>
        private const string RankedSeats = """
            attending AS (
                SELECT e."Id" AS event_id, a."Id" AS option_id
                FROM "Events" AS e
                JOIN LATERAL (
                    SELECT "Id"
                    FROM "RsvpOptions"
                    WHERE "EventId" = e."Id"
                    ORDER BY "IsAttending" DESC, "SortOrder", "Id"
                    LIMIT 1
                ) AS a ON TRUE
            ),
            ranked AS (
                SELECT r."Id", r."EventId", r."UserId", r."OptionId", r."Waitlisted",
                       ROW_NUMBER() OVER (
                           PARTITION BY r."EventId", r."UserId"
                           ORDER BY CASE WHEN NOT r."Waitlisted" AND r."OptionId" = a.option_id THEN 0 ELSE 1 END,
                                    r."CreatedAt", r."Id") AS seat
                FROM "Rsvps" AS r
                LEFT JOIN attending AS a ON a.event_id = r."EventId"
            )
            """;

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
            // A discarded SEATED row on a role-bearing option of a live event loses that role for
            // the member unless the kept row (when itself seated) carries the same role — one
            // RevokeAttendeeRole (type 11) per (event, member, role), the way the app's own
            // end/delete sweep would enqueue it, since the downgraded code can no longer see those
            // seats. Waitlisted rows never held a role and enqueue nothing.
            migrationBuilder.Sql($"""
                WITH {RankedSeats}
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

            // A posted embed would keep showing the discarded choices: one SyncEventMessage
            // (type 3) per affected event that has a message, so the bot re-renders it from the
            // collapsed rows. Ready reconciles live lists on its own, but not event messages.
            migrationBuilder.Sql($"""
                WITH {RankedSeats}
                INSERT INTO "Deliveries" ("Id", "Type", "ChannelId", "PayloadJson", "DueAt", "Status", "Attempts", "CreatedAt")
                SELECT gen_random_uuid(),
                       3,
                       e."ChannelId",
                       json_build_object('EventId', e."Id")::text,
                       now(),
                       0,
                       0,
                       now()
                FROM "Events" AS e
                WHERE e."MessageId" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM ranked AS r WHERE r."EventId" = e."Id" AND r.seat > 1);
                """);

            // Collapse each member back to one RSVP per event BEFORE the old unique index is
            // restored, or its creation would fail on the very rows it forbids.
            migrationBuilder.Sql($"""
                WITH {RankedSeats}
                DELETE FROM "Rsvps"
                WHERE "Id" IN (SELECT "Id" FROM ranked WHERE seat > 1);
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
