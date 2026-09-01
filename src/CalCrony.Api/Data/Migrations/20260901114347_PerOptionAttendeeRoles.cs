using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Moves the attendee role from the event onto the RSVP option, so one event can hand
    /// out a different role per choice (Tank/Healer/DPS). The column is added and backfilled BEFORE
    /// the old ones are dropped, so no configured role is lost: an event's role lands on its
    /// attending option, and a series' role folds into the option template it already keeps
    /// capacities in.</summary>
    /// <inheritdoc />
    public partial class PerOptionAttendeeRoles : Migration
    {
        /// <summary>The default option set a series with no explicit template spawns, matching
        /// EventEndpoints.DefaultRsvpOptions and RsvpPolicy.SerializeSpecs. Split around the role
        /// so the series' value can be spliced into the attending ("Going") entry.</summary>
        private const string DefaultTemplatePrefix =
            """[{"Emote":"✅","Label":"Going","Capacity":null,"IsAttending":true,"AttendeeRoleId":""";

        private const string DefaultTemplateSuffix =
            """},{"Emote":"❌","Label":"Not going","Capacity":null,"IsAttending":false,"AttendeeRoleId":null},{"Emote":"🤔","Label":"Maybe","Capacity":null,"IsAttending":false,"AttendeeRoleId":null}]""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AttendeeRoleId",
                table: "RsvpOptions",
                type: "bigint",
                nullable: true);

            // An event's single role belonged to whoever was seated on its attending option, so
            // that option inherits it. The attending flag is the primary key of that choice, with
            // the same lowest-SortOrder fallback RsvpPolicy.AttendingOption applies to rows that
            // predate the flag.
            migrationBuilder.Sql("""
                UPDATE "RsvpOptions" AS o
                SET "AttendeeRoleId" = e."AttendeeRoleId"
                FROM "Events" AS e
                JOIN LATERAL (
                    SELECT "Id"
                    FROM "RsvpOptions"
                    WHERE "EventId" = e."Id"
                    ORDER BY "IsAttending" DESC, "SortOrder", "Id"
                    LIMIT 1
                ) AS attending ON TRUE
                WHERE e."AttendeeRoleId" IS NOT NULL AND o."Id" = attending."Id";
                """);

            // A series with no option template spawned the default set; materialize that set so
            // the role has somewhere to live, exactly as the API would now serialize it.
            migrationBuilder.Sql($"""
                UPDATE "EventSeries"
                SET "RsvpOptionsJson" =
                    '{DefaultTemplatePrefix}' || "AttendeeRoleId"::text || '{DefaultTemplateSuffix}'
                WHERE "AttendeeRoleId" IS NOT NULL AND "RsvpOptionsJson" IS NULL;
                """);

            // A series that already had a template gets the role spliced into its attending spec
            // (first entry when none is flagged, matching the same fallback).
            migrationBuilder.Sql("""
                UPDATE "EventSeries" AS s
                SET "RsvpOptionsJson" = rewritten.json
                FROM (
                    SELECT
                        e."Id" AS series_id,
                        (
                            SELECT jsonb_agg(
                                       CASE
                                           WHEN spec.ord = target.ord
                                           THEN spec.val || jsonb_build_object('AttendeeRoleId', e."AttendeeRoleId")
                                           ELSE spec.val
                                       END
                                       ORDER BY spec.ord)::text
                            FROM jsonb_array_elements(e."RsvpOptionsJson"::jsonb)
                                 WITH ORDINALITY AS spec(val, ord)
                        ) AS json
                    FROM "EventSeries" AS e
                    CROSS JOIN LATERAL (
                        SELECT COALESCE(
                            (SELECT MIN(a.ord)
                             FROM jsonb_array_elements(e."RsvpOptionsJson"::jsonb)
                                  WITH ORDINALITY AS a(val, ord)
                             WHERE (a.val ->> 'IsAttending')::boolean),
                            1) AS ord
                    ) AS target
                    WHERE e."AttendeeRoleId" IS NOT NULL AND e."RsvpOptionsJson" IS NOT NULL
                ) AS rewritten
                WHERE s."Id" = rewritten.series_id AND rewritten.json IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "AttendeeRoleId",
                table: "EventSeries");

            migrationBuilder.DropColumn(
                name: "AttendeeRoleId",
                table: "Events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AttendeeRoleId",
                table: "EventSeries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AttendeeRoleId",
                table: "Events",
                type: "bigint",
                nullable: true);

            // Collapse back to one role per event/series: the attending option's wins, and roles
            // on the other options are dropped — the pre-v2 schema cannot express them.
            migrationBuilder.Sql("""
                UPDATE "Events" AS e
                SET "AttendeeRoleId" = (
                    SELECT o."AttendeeRoleId"
                    FROM "RsvpOptions" AS o
                    WHERE o."EventId" = e."Id"
                    ORDER BY o."IsAttending" DESC, o."SortOrder", o."Id"
                    LIMIT 1
                );
                """);

            migrationBuilder.Sql("""
                UPDATE "EventSeries" AS s
                SET "AttendeeRoleId" = (
                    SELECT (spec.val ->> 'AttendeeRoleId')::bigint
                    FROM jsonb_array_elements(s."RsvpOptionsJson"::jsonb) WITH ORDINALITY AS spec(val, ord)
                    ORDER BY (spec.val ->> 'IsAttending')::boolean DESC NULLS LAST, spec.ord
                    LIMIT 1
                )
                WHERE s."RsvpOptionsJson" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "AttendeeRoleId",
                table: "RsvpOptions");
        }
    }
}
